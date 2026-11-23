namespace PycRune.Decompiler;

using PycRune.AST;
using PycRune.Analysis;
using PycRune.Model;

public sealed class StructuralDecompiler
{
    private sealed class TryScope
    {
        public required int StartOffset { get; init; }
        public required int BodyEndOffset { get; set; }
        public List<int> ExceptHandlers { get; init; } = [];
        public int? ElseStartOffset { get; set; }
        public int? ElseEndOffset { get; set; }
        public int? PostElseResumeOffset { get; set; }
        public int? FinallyHandler { get; set; }
        public required int EndOffset { get; set; }
        public int Depth { get; set; }
    }

    private readonly PythonCodeObject _code;
    private readonly IReadOnlyList<PythonInstruction> _instrs;
    private readonly Dictionary<int, int> _offsetToIndex = [];
    private readonly List<TryScope> _tryScopes = [];
    private readonly HashSet<TryScope> _activeScopes = [];
    private readonly HashSet<int> _handlerStopIndices = [];
    private readonly Stack<int> _activeLoopHeaders = new();
    private readonly Stack<int> _activeLoopExits = new();
        private bool IsActiveLoopHeader(int offset)
    {
        if (_activeLoopHeaders.Contains(offset)) return true;
        if (!_offsetToIndex.TryGetValue(offset, out var idx)) return false;
        while (idx < _instrs.Count && _instrs[idx].OpCode == OpCodes.EXTENDED_ARG) idx++;
        return idx < _instrs.Count && _activeLoopHeaders.Contains(_instrs[idx].Offset);
    }


    private readonly HashSet<string> _clearedFastVars = new(StringComparer.Ordinal);
    private readonly List<string> _typeParams = new();
    private IReadOnlyList<string>? _activeKwNames;
    private int _lastInlinedExitStart = -1;
    private int _lastInlinedExitLength = -1;
    private int _pendingBreakExitStart = -1;
    private int _pendingBreakExitLength = -1;

    private void SkipSyntheticVarCleanup(ref int p)
    {
        if (p + 2 < _instrs.Count &&
            _instrs[p].OpCode == OpCodes.LOAD_CONST && _instrs[p].ArgVal == null &&
            _instrs[p + 1].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME &&
            _instrs[p + 2].OpCode is OpCodes.DELETE_FAST or OpCodes.DELETE_NAME)
        {
            var s = _instrs[p + 1].ArgRepr ?? _instrs[p + 1].ArgVal?.ToString();
            var d = _instrs[p + 2].ArgRepr ?? _instrs[p + 2].ArgVal?.ToString();
            if (s == d)
            {
                p += 3;
            }
        }
    }

    public StructuralDecompiler(PythonCodeObject code)
    {
        _code = code;
        _instrs = code.Instructions;
        for (var i = 0; i < _instrs.Count; i++)
        {
            _offsetToIndex.TryAdd(_instrs[i].Offset, i);


            if (_instrs[i].OpCode == OpCodes.LOAD_FAST_AND_CLEAR)
            {
                var clearName = _instrs[i].ArgRepr ?? _instrs[i].ArgVal?.ToString() ?? "";
                if (clearName.StartsWith("NULL + ")) clearName = clearName[7..];
                if (!string.IsNullOrEmpty(clearName)) _clearedFastVars.Add(clearName);
            }
        }

        BuildTryScopes(code.ExceptionTable);
    }

        private bool IsPep709CleanupReraise(int idx)
    {
        if (idx < 1 || idx >= _instrs.Count) return false;
        if (_instrs[idx].OpCode != OpCodes.RERAISE || _instrs[idx].OpArg != 0) return false;
        var prev = _instrs[idx - 1];
        if (prev.OpCode is not (OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_DEREF)) return false;
        var name = prev.ArgRepr ?? prev.ArgVal?.ToString() ?? "";
        if (string.IsNullOrEmpty(name) || !_clearedFastVars.Contains(name)) return false;
        return idx + 2 < _instrs.Count &&
               _instrs[idx + 1].OpCode == OpCodes.SWAP &&
               _instrs[idx + 2].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME &&
               (_instrs[idx + 2].ArgRepr ?? _instrs[idx + 2].ArgVal?.ToString() ?? "") == name;
    }

        private int FindExceptHandlerRegionEnd(int handlerOffset, int upperBound, int fallbackEnd)
    {
        var maxEnd = fallbackEnd;
        var targets = new List<int>();
        foreach (var e in _code.ExceptionTable)
        {
            if (e.Start < handlerOffset || e.Start >= upperBound) continue;
            if (e.End > maxEnd) maxEnd = e.End;
            targets.Add(e.Target);
        }

        foreach (var t in targets.Distinct())
        {
            if (!_offsetToIndex.TryGetValue(t, out var tIdx)) continue;


            if (_instrs[tIdx].OpCode == OpCodes.PUSH_EXC_INFO) continue;
            var q = tIdx;
            while (q < _instrs.Count)
            {
                if (_instrs[q].OpCode == OpCodes.RERAISE)
                {
                    var after = _instrs[q].Offset + 2;
                    if (after > maxEnd) maxEnd = after;
                    break;
                }
                q++;
            }
        }

        return maxEnd;
    }

    private void BuildTryScopes(IReadOnlyList<ExceptionTableEntry> entries)
    {
        _tryScopes.Clear();

        var userEntries = entries
            .Where(e => !e.LastI && _offsetToIndex.TryGetValue(e.Target, out var tIdx) && _instrs[tIdx].OpCode == OpCodes.PUSH_EXC_INFO && (tIdx + 1 >= _instrs.Count || _instrs[tIdx + 1].OpCode != OpCodes.WITH_EXCEPT_START))
            .OrderBy(e => e.Start)
            .ThenByDescending(e => e.End)
            .ToList();

        if (userEntries.Count == 0 && !_instrs.Any(i => i.OpCode == OpCodes.PUSH_EXC_INFO)) return;

        var elseStarts = new HashSet<int>();
        foreach (var e1 in userEntries)
        {
            if (!IsFinallyHandler(e1.Target))
            {
                foreach (var e2 in userEntries)
                {
                    if (IsFinallyHandler(e2.Target) && e2.Start >= e1.End && e2.Start < e1.Target)
                    {
                        if (_offsetToIndex.TryGetValue(e2.Start, out var esIdx) &&
                            _offsetToIndex.TryGetValue(e2.End, out var eeIdx) &&
                            HasRealStatementsBetween(_instrs, esIdx, eeIdx))
                        {
                            elseStarts.Add(e2.Start);
                        }
                    }
                }
            }
        }

        var entryStarts = userEntries.Where(e => !elseStarts.Contains(e.Start)).Select(e => e.Start).ToHashSet();
        var jumpTargets = new HashSet<int>();
        foreach (var ins in _instrs)
        {
            if (ins.TargetOffset.HasValue && ins.OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_FALSE)
            {
                jumpTargets.Add(ins.TargetOffset.Value);
            }
        }

        var clusters = new List<(List<int> Nops, int BodyStart)>();
        var i = 0;
        while (i < _instrs.Count)
        {
            if (_instrs[i].OpCode == OpCodes.NOP)
            {
                var k = i;
                var nops = new List<int>();
                while (k < _instrs.Count && _instrs[k].OpCode == OpCodes.NOP)
                {
                    if (k > i && (jumpTargets.Contains(_instrs[k].Offset) || entryStarts.Contains(_instrs[k].Offset)))
                    {
                        break;
                    }
                    nops.Add(_instrs[k].Offset);
                    k++;
                }
                if (k < _instrs.Count && entryStarts.Contains(_instrs[k].Offset) && nops.Count > 0)
                {
                    var nextOff = _instrs[k].Offset;


                    if (userEntries.Any(e => IsFinallyHandler(e.Target) && e.Encloses(nops[0])))
                    {
                        i = k;
                        continue;
                    }
                    if (i > 0 && _instrs[i - 1].OpCode is OpCodes.RERAISE or OpCodes.PUSH_EXC_INFO)
                    {
                        if (userEntries.Any(e => IsFinallyHandler(e.Target) && e.Start < nextOff && e.Target > nextOff))
                        {
                            i++;
                            continue;
                        }
                    }
                    clusters.Add((nops, nextOff));
                    i = k;
                    continue;
                }
            }
            i++;
        }

        var allExceptTargets = new HashSet<int>(userEntries.Where(e => !IsFinallyHandler(e.Target)).Select(e => e.Target));
        foreach (var ins in _instrs)
        {
            if (ins.OpCode == OpCodes.PUSH_EXC_INFO && !IsFinallyHandler(ins.Offset))
            {
                allExceptTargets.Add(ins.Offset);
            }
        }
        var exceptTargets = allExceptTargets.OrderBy(t => t).ToList();
        var exceptHandlerEnds = new Dictionary<int, int>();
        foreach (var h in exceptTargets)
        {
            if (_offsetToIndex.TryGetValue(h, out var hIdx))
            {
                var scan = hIdx;
                var nesting = 0;
                var endOff = -1;
                while (scan < _instrs.Count)
                {
                    if (_instrs[scan].OpCode == OpCodes.PUSH_EXC_INFO && scan != hIdx) nesting++;
                    else if (_instrs[scan].OpCode == OpCodes.POP_EXCEPT)
                    {


                        var isCleanupTail = scan + 1 < _instrs.Count && _instrs[scan + 1].OpCode == OpCodes.RERAISE;
                        if (isCleanupTail)
                        {
                            if (nesting > 0) nesting--;
                        }
                        else if (nesting == 0) endOff = _instrs[scan].Offset;
                        else if (nesting > 0) nesting--;
                    }
                    else if (_instrs[scan].OpCode == OpCodes.RERAISE && nesting == 0)
                    {
                        if (endOff >= 0) break;
                    }
                    scan++;
                }
                if (endOff >= 0) exceptHandlerEnds[h] = endOff;
            }
        }

        var scopes = new List<TryScope>();
        var claimedTargets = new HashSet<int>();
        var clusterPairs = new List<(TryScope Outer, TryScope Inner)>();
        var waitingOuters = new List<TryScope>();

        for (var cIdx = 0; cIdx < clusters.Count; cIdx++)
        {
            var cluster = clusters[cIdx];
            var nops = cluster.Nops;
            var bs = cluster.BodyStart;

            var nopsStart = nops[0];
            var nextBs = cIdx + 1 < clusters.Count ? clusters[cIdx + 1].Nops[0] : _instrs[^1].Offset + 2;
            var enclosingH = exceptTargets.Where(h => h < nopsStart && exceptHandlerEnds.TryGetValue(h, out var ehEnd) && nopsStart < ehEnd).ToList();
            var encEnd = enclosingH.Count > 0 && exceptHandlerEnds.TryGetValue(enclosingH[^1], out var eEnd) ? (int?)eEnd : null;
            var limit = encEnd.HasValue ? Math.Min(nextBs, encEnd.Value) : nextBs;


            var cEntries = userEntries
                .Where(e => bs <= e.Start && e.Start < limit && !claimedTargets.Contains(e.Target))
                .ToList();
            var targets = new List<int>();
            foreach (var e in cEntries)
            {
                if (!targets.Contains(e.Target)) targets.Add(e.Target);
            }

            var bsDepthEntries = userEntries.Where(e => e.Start <= bs && bs < e.End).Select(e => e.Depth).ToList();
            var bsDepth = bsDepthEntries.Count > 0 ? bsDepthEntries[0] : 0;

            if (nops.Count == 1)
            {
                var s = new TryScope
                {
                    StartOffset = nops[0],
                    BodyEndOffset = bs,
                    EndOffset = bs,
                    Depth = bsDepth
                };

                foreach (var t in targets)
                {
                    if (!IsFinallyHandler(t) && s.ExceptHandlers.Count == 0)
                    {
                        s.ExceptHandlers.Add(t);
                        claimedTargets.Add(t);
                    }
                    else if (IsFinallyHandler(t) && !s.FinallyHandler.HasValue)
                    {
                        s.FinallyHandler = t;
                        claimedTargets.Add(t);
                    }
                }

                if (s.ExceptHandlers.Count == 0 && s.FinallyHandler.HasValue)
                {
                    var intermediateExcept = exceptTargets.FirstOrDefault(h => h > bs && h < s.FinallyHandler.Value && !claimedTargets.Contains(h) && userEntries.Any(e => e.Target == h && e.Start == bs));
                    if (intermediateExcept > 0)
                    {
                        s.ExceptHandlers.Add(intermediateExcept);
                        claimedTargets.Add(intermediateExcept);
                    }
                }

                var sT = s.ExceptHandlers.Count > 0 ? s.ExceptHandlers[0] : s.FinallyHandler;
                if (sT.HasValue)
                {


                    var nestedHandlerRegions = new List<(int Start, int End)>();
                    foreach (var t in entries.Where(e => e.Target < sT.Value).Select(e => e.Target).Distinct())
                    {
                        if (!_offsetToIndex.TryGetValue(t, out var nestedIdx)) continue;
                        if (_instrs[nestedIdx].OpCode != OpCodes.PUSH_EXC_INFO) continue;
                        var nestedExit = FindHandlerNormalExit(nestedIdx, _instrs.Count);
                        if (nestedExit < 0) continue;
                        nestedHandlerRegions.Add((t, _instrs[nestedExit].Offset));
                    }
                    var targetEntries = userEntries.Where(e => e.Target == sT.Value).ToList();
                    var sEnds = targetEntries
                        .Where(e => !IsCleanupOnlyEntry(e))
                        .Where(e => !nestedHandlerRegions.Any(r =>
                            e.Start >= r.Start && e.Start <= r.End && e.End <= r.End))
                        .Select(e => e.End)
                        .ToList();
                    if (sEnds.Count == 0)
                    {
                        sEnds = targetEntries.Where(e => !IsCleanupOnlyEntry(e)).Select(e => e.End).ToList();
                    }
                    var maxEnd = sEnds.Count > 0 ? sEnds.Max() : bs;
                    if (!_offsetToIndex.ContainsKey(maxEnd))
                    {
                        maxEnd = sT.Value;
                    }
                    if (s.FinallyHandler.HasValue && s.ExceptHandlers.Count == 0 && _offsetToIndex.TryGetValue(s.FinallyHandler.Value, out var fhIdx))
                    {
                        var fhStart = fhIdx;
                        if (fhStart < _instrs.Count && _instrs[fhStart].OpCode == OpCodes.PUSH_EXC_INFO) fhStart++;
                        var fhEnd = fhStart;
                        var nesting = 0;
                        while (fhEnd < _instrs.Count)
                        {
                            if (_instrs[fhEnd].OpCode == OpCodes.PUSH_EXC_INFO) nesting++;
                            else if (_instrs[fhEnd].OpCode == OpCodes.POP_EXCEPT) { if (nesting > 0) nesting--; }
                            else if (_instrs[fhEnd].OpCode is OpCodes.RERAISE or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                            {
                                if (nesting == 0) break;
                            }
                            fhEnd++;
                        }
                        var finLen = fhEnd - fhStart;
                        if (finLen > 0)
                        {
                            var minScan = _offsetToIndex.TryGetValue(bs, out var bIdx) ? bIdx : 0;
                            var maxScan = Math.Min(fhStart - 1, _instrs.Count - finLen);
                            for (var scan = maxScan; scan >= minScan; scan--)
                            {
                                var match = true;
                                if (scan + finLen > fhStart) continue;
                                for (var m = 0; m < finLen; m++)
                                {
                                    if (_instrs[scan + m].OpCode != _instrs[fhStart + m].OpCode &&
                                        !(_instrs[scan + m].OpCode == OpCodes.JUMP_FORWARD && _instrs[fhStart + m].OpCode == OpCodes.RERAISE))
                                    {
                                        match = false;
                                        break;
                                    }
                                }
                                if (match)
                                {
                                    var normalFinOffset = _instrs[scan].Offset;
                                    if (normalFinOffset > bs)
                                    {


                                        maxEnd = normalFinOffset;
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    s.BodyEndOffset = maxEnd;
                    if (s.ExceptHandlers.Count > 0 && !s.FinallyHandler.HasValue)
                    {


                        var finCandidate = userEntries
                            .Where(e => IsFinallyHandler(e.Target) && !claimedTargets.Contains(e.Target) &&
                                        e.Target > s.ExceptHandlers[0] &&
                                        e.Start >= s.BodyEndOffset && e.Start < s.ExceptHandlers[0])
                            .GroupBy(e => e.Target)
                            .OrderBy(g => g.Min(e => e.Start))
                            .Select(g => g.Key)
                            .FirstOrDefault();
                        if (finCandidate > 0)
                        {
                            s.FinallyHandler = finCandidate;
                            claimedTargets.Add(finCandidate);
                        }
                    }
                    scopes.Add(s);
                }
            }
            else if (nops.Count == 2)
            {
                var outerS = new TryScope
                {
                    StartOffset = nops[0],
                    BodyEndOffset = bs,
                    EndOffset = bs,
                    Depth = bsDepth
                };
                var innerS = new TryScope
                {
                    StartOffset = nops[1],
                    BodyEndOffset = bs,
                    EndOffset = bs,
                    Depth = bsDepth
                };
                clusterPairs.Add((outerS, innerS));

                var cExcs = targets.Where(t => !IsFinallyHandler(t)).ToList();
                var cFins = targets.Where(IsFinallyHandler).ToList();

                if (cExcs.Count >= 2 && cFins.Count >= 2)
                {
                    innerS.ExceptHandlers.Add(cExcs[0]);
                    claimedTargets.Add(cExcs[0]);
                    innerS.FinallyHandler = cFins[0];
                    claimedTargets.Add(cFins[0]);
                    outerS.ExceptHandlers.Add(cExcs[1]);
                    claimedTargets.Add(cExcs[1]);
                    outerS.FinallyHandler = cFins[1];
                    claimedTargets.Add(cFins[1]);
                    var innerBsEnds = userEntries
                        .Where(e => e.Target == cExcs[0] && e.Start <= bs && bs < e.End)
                        .Select(e => e.End)
                        .ToList();
                    innerS.BodyEndOffset = innerBsEnds.Count > 0 ? innerBsEnds.Max() : bs;
                }
                else if (targets.Count == 2)
                {
                    var firstT = targets[0];
                    var secondT = targets[1];
                    if (IsFinallyHandler(firstT)) innerS.FinallyHandler = firstT;
                    else innerS.ExceptHandlers.Add(firstT);
                    claimedTargets.Add(firstT);

                    if (IsFinallyHandler(secondT)) outerS.FinallyHandler = secondT;
                    else outerS.ExceptHandlers.Add(secondT);
                    claimedTargets.Add(secondT);
                }
                else if (targets.Count == 1)
                {
                    var firstT = targets[0];
                    if (IsFinallyHandler(firstT)) innerS.FinallyHandler = firstT;
                    else innerS.ExceptHandlers.Add(firstT);
                    claimedTargets.Add(firstT);
                    waitingOuters.Add(outerS);
                }
                else if (targets.Count >= 3)
                {
                    var firstT = targets[0];
                    if (IsFinallyHandler(firstT)) innerS.FinallyHandler = firstT;
                    else innerS.ExceptHandlers.Add(firstT);
                    claimedTargets.Add(firstT);

                    var rem = targets.Skip(1).ToList();
                    var remExcs = rem.Where(t => !IsFinallyHandler(t)).ToList();
                    var remFins = rem.Where(IsFinallyHandler).ToList();
                    if (remExcs.Count > 0)
                    {
                        var firstExc = remExcs[0];
                        outerS.ExceptHandlers.Add(firstExc);
                        claimedTargets.Add(firstExc);
                        foreach (var f in remFins)
                        {
                            if (f < firstExc && !innerS.FinallyHandler.HasValue)
                            {
                                innerS.FinallyHandler = f;
                                claimedTargets.Add(f);
                            }
                            else if (f > firstExc && !outerS.FinallyHandler.HasValue)
                            {
                                outerS.FinallyHandler = f;
                                claimedTargets.Add(f);
                            }
                        }
                    }
                    else
                    {
                        foreach (var f in remFins)
                        {
                            if (!outerS.FinallyHandler.HasValue)
                            {
                                outerS.FinallyHandler = f;
                                claimedTargets.Add(f);
                            }
                        }
                    }
                }

                var innerT = innerS.ExceptHandlers.Count > 0 ? innerS.ExceptHandlers[0] : innerS.FinallyHandler;
                if (innerT.HasValue)
                {
                    var innerBsEnds = userEntries
                        .Where(e => e.Target == innerT.Value)
                        .Select(e => e.End)
                        .ToList();
                    innerS.BodyEndOffset = innerBsEnds.Count > 0 ? innerBsEnds.Max() : bs;
                }

                scopes.Add(innerS);
                scopes.Add(outerS);
            }
        }

        for (var wIdx = waitingOuters.Count - 1; wIdx >= 0; wIdx--)
        {
            var outerS = waitingOuters[wIdx];
            var innerS = clusterPairs.First(p => p.Outer == outerS).Inner;
            var postTargets = userEntries
                .Where(e => !claimedTargets.Contains(e.Target) && e.Target > outerS.StartOffset && e.Target < outerS.StartOffset + 600)
                .Select(e => e.Target)
                .Distinct()
                .OrderBy(t => t)
                .ToList();
            var postExcs = postTargets.Where(t => !IsFinallyHandler(t)).ToList();
            var postFins = postTargets.Where(IsFinallyHandler).ToList();
            if (postExcs.Count > 0)
            {
                var firstExc = postExcs[0];
                outerS.ExceptHandlers.Add(firstExc);
                claimedTargets.Add(firstExc);
                foreach (var f in postFins)
                {
                    if (f < firstExc && !innerS.FinallyHandler.HasValue)
                    {
                        innerS.FinallyHandler = f;
                        claimedTargets.Add(f);
                    }
                    else if (f > firstExc && !outerS.FinallyHandler.HasValue)
                    {
                        outerS.FinallyHandler = f;
                        claimedTargets.Add(f);
                    }
                }
            }
            else
            {
                foreach (var f in postFins)
                {
                    if (!outerS.FinallyHandler.HasValue)
                    {
                        outerS.FinallyHandler = f;
                        claimedTargets.Add(f);
                    }
                }
            }
        }

        scopes.RemoveAll(sc => sc.ExceptHandlers.Count == 0 && !sc.FinallyHandler.HasValue);

        foreach (var s in scopes)
        {
            if (s.ExceptHandlers.Count > 0 && !clusterPairs.Any(p => p.Outer == s))
            {
                var firstH = s.ExceptHandlers[0];
                if (_offsetToIndex.TryGetValue(firstH, out var hIdx) && _offsetToIndex.TryGetValue(s.BodyEndOffset, out var bodyEndIdx))
                {
                    for (var k = hIdx - 1; k > bodyEndIdx; k--)
                    {
                        if (_instrs[k].OpCode == OpCodes.JUMP_FORWARD && (_instrs[k].TargetOffset ?? 0) > firstH && HasRealStatementsBetween(_instrs, bodyEndIdx, k))
                        {
                            s.ElseStartOffset = s.BodyEndOffset;
                            s.ElseEndOffset = _instrs[k].Offset;
                            break;
                        }
                    }

                    if (!s.ElseStartOffset.HasValue && s.FinallyHandler.HasValue)
                    {
                        var elseEntry = userEntries.FirstOrDefault(e =>
                            e.Target == s.FinallyHandler.Value &&
                            e.Start >= s.BodyEndOffset &&
                            e.Start < firstH &&
                            _offsetToIndex.TryGetValue(e.Start, out var esIdx) &&
                            _offsetToIndex.TryGetValue(e.End, out var eeIdx) &&
                            HasRealStatementsBetween(_instrs, esIdx, eeIdx));

                        if (elseEntry != null)
                        {
                            s.ElseStartOffset = elseEntry.Start;
                            s.ElseEndOffset = elseEntry.End;
                        }
                    }


                    if (!s.ElseStartOffset.HasValue)
                    {
                        var elseEntry = userEntries.FirstOrDefault(e =>
                            e.Start == s.BodyEndOffset &&
                            e.Target != firstH &&
                            IsFinallyHandler(e.Target) &&
                            e.Start < firstH &&
                            _offsetToIndex.TryGetValue(e.Start, out var esIdx) &&
                            _offsetToIndex.TryGetValue(e.End, out var eeIdx) &&
                            HasRealStatementsBetween(_instrs, esIdx, eeIdx));

                        if (elseEntry != null)
                        {
                            s.ElseStartOffset = elseEntry.Start;
                            s.ElseEndOffset = elseEntry.End;
                        }
                    }


                }
            }
        }


        foreach (var s in scopes)
        {
            if (s.ExceptHandlers.Count == 0 || s.ElseStartOffset.HasValue || s.FinallyHandler.HasValue) continue;
            if (clusterPairs.Any(p => p.Outer == s)) continue;
            var firstH = s.ExceptHandlers[0];
            if (!_offsetToIndex.TryGetValue(firstH, out var hIdx)) continue;
            if (!_offsetToIndex.TryGetValue(s.BodyEndOffset, out var bodyEndIdx)) continue;
            if (hIdx <= bodyEndIdx) continue;
            if (!HasRealStatementsBetween(_instrs, bodyEndIdx, hIdx)) continue;
            if (!exceptHandlerEnds.TryGetValue(firstH, out var popOffset)) continue;
            if (!_offsetToIndex.TryGetValue(popOffset, out var popIdx)) continue;

            var contIdx = popIdx + 1;
            SkipSyntheticVarCleanup(ref contIdx);
            if (contIdx >= _instrs.Count) continue;

            var cont = _instrs[contIdx];
            var regionEnd = hIdx;
            if (cont.OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                cont.TargetOffset is int backTarget &&
                _offsetToIndex.TryGetValue(backTarget, out var backIdx))
            {
                if (backIdx >= bodyEndIdx && backIdx < hIdx)
                {


                    continue;
                }

                if (backIdx < bodyEndIdx)
                {


                    for (var k = bodyEndIdx; k < hIdx; k++)
                    {
                        var op = _instrs[k].OpCode;
                        if ((op is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT or OpCodes.JUMP_FORWARD) &&
                            _instrs[k].TargetOffset is int t &&
                            _offsetToIndex.TryGetValue(t, out var tIdx) &&
                            (tIdx < bodyEndIdx || tIdx >= hIdx))
                        {
                            regionEnd = k;
                            break;
                        }
                    }
                }
            }

            if (regionEnd <= bodyEndIdx) continue;

            var runEnd = contIdx;
            var nesting = 0;
            while (runEnd < _instrs.Count)
            {
                if (_instrs[runEnd].OpCode == OpCodes.PUSH_EXC_INFO) nesting++;
                else if (_instrs[runEnd].OpCode == OpCodes.POP_EXCEPT) { if (nesting > 0) nesting--; }
                else if (_instrs[runEnd].OpCode == OpCodes.RERAISE && nesting == 0) break;
                runEnd++;
            }

            var regionLen = regionEnd - bodyEndIdx;
            var maxDup = Math.Min(regionLen, runEnd - contIdx);
            var dupLen = 0;
            for (var len = 1; len <= maxDup; len++)
            {
                var match = true;
                for (var m = 0; m < len; m++)
                {
                    var a = _instrs[regionEnd - len + m];
                    var b = _instrs[contIdx + m];
                    if (a.OpCode != b.OpCode) { match = false; break; }
                    if (a.OpCode is not (OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT) &&
                        !Equals(a.ArgVal, b.ArgVal)) { match = false; break; }
                }
                if (match) dupLen = len;
            }

            if (dupLen >= regionLen) continue;

            var elseEndIdx = regionEnd - dupLen;
            s.ElseStartOffset = _instrs[bodyEndIdx].Offset;
            s.ElseEndOffset = _instrs[elseEndIdx].Offset;
            if (dupLen > 0)
            {
                s.PostElseResumeOffset = s.ElseEndOffset;
            }
        }

        foreach (var s in scopes)
        {
            var maxEnd = Math.Max(s.BodyEndOffset, s.ElseEndOffset ?? 0);
            foreach (var h in s.ExceptHandlers)
            {
                if (_offsetToIndex.TryGetValue(h, out var hIdx))
                {
                    if (hIdx > 0 && _instrs[hIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                    {
                        var hJumpTarget = _instrs[hIdx - 1].TargetOffset ?? 0;
                        if (hJumpTarget > maxEnd) maxEnd = hJumpTarget;
                    }

                    var curIdx = hIdx;
                    var nesting = 0;
                    while (curIdx < _instrs.Count)
                    {
                        var cur = _instrs[curIdx];
                        var nxt = curIdx + 1 < _instrs.Count ? _instrs[curIdx + 1] : null;

                        if (cur.OpCode == OpCodes.PUSH_EXC_INFO && curIdx != hIdx)
                        {
                            nesting++;
                        }
                        else if (cur.OpCode == OpCodes.JUMP_FORWARD && nesting == 0)
                        {
                            var target = cur.TargetOffset ?? 0;
                            if (target > maxEnd) maxEnd = target;
                        }
                        else if (cur.OpCode == OpCodes.POP_EXCEPT)
                        {
                            if (nxt != null && nxt.OpCode == OpCodes.RERAISE)
                            {
                                if (nesting > 0)
                                {
                                    nesting--;
                                    curIdx += 2;
                                    continue;
                                }
                                else
                                {
                                    var after = nxt.Offset + 2;
                                    if (after > maxEnd) maxEnd = after;
                                    break;
                                }
                            }
                            else if (nesting == 0)
                            {
                                var postPop = curIdx + 1;
                                SkipSyntheticVarCleanup(ref postPop);
                                var pIns = postPop < _instrs.Count ? _instrs[postPop] : null;

                                if (pIns != null && pIns.OpCode == OpCodes.JUMP_FORWARD)
                                {
                                    var target = pIns.TargetOffset ?? 0;
                                    if (target > maxEnd) maxEnd = target;
                                }
                                else if (pIns != null && pIns.OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
                                {
                                    var target = pIns.TargetOffset ?? 0;
                                    if (target < pIns.Offset && target >= s.BodyEndOffset)
                                    {
                                        maxEnd = target;
                                        break;
                                    }
                                }
                                else if (pIns != null && pIns.OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                                {
                                    var after = pIns.Offset + 2;
                                    if (after > maxEnd) maxEnd = after;
                                    if (exceptHandlerEnds.TryGetValue(h, out var ehEnd) && cur.Offset >= ehEnd)
                                    {
                                        break;
                                    }
                                }
                                else
                                {
                                    var after = (postPop > curIdx + 1 ? _instrs[postPop - 1].Offset : cur.Offset) + 2;
                                    if (after > maxEnd) maxEnd = after;
                                    break;
                                }
                            }
                        }
                        else if (cur.OpCode == OpCodes.RERAISE && nesting == 0)
                        {
                            if (IsPep709CleanupReraise(curIdx))
                            {


                                var handlerEnd = exceptHandlerEnds.TryGetValue(h, out var ehEndScan)
                                    ? ehEndScan
                                    : maxEnd;
                                var regionEnd = FindExceptHandlerRegionEnd(h, handlerEnd, maxEnd);
                                if (regionEnd > maxEnd) maxEnd = regionEnd;
                                curIdx++;
                                continue;
                            }
                            var after = cur.Offset + 2;
                            if (cur.OpArg == 0 && curIdx + 3 < _instrs.Count && _instrs[curIdx + 1].OpCode == OpCodes.COPY && _instrs[curIdx + 2].OpCode == OpCodes.POP_EXCEPT && _instrs[curIdx + 3].OpCode == OpCodes.RERAISE)
                            {
                                after = _instrs[curIdx + 3].Offset + 2;
                            }
                            if (after > maxEnd) maxEnd = after;
                            break;
                        }
                        curIdx++;
                    }
                }
            }
            s.EndOffset = maxEnd;
        }

        var unclaimedFins = userEntries
            .Where(e => IsFinallyHandler(e.Target) && !claimedTargets.Contains(e.Target))
            .Select(e => e.Target)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        foreach (var f in unclaimedFins)
        {
            var fDepthEntries = userEntries.Where(e => e.Target == f).Select(e => e.Depth).ToList();
            var fDepth = fDepthEntries.Count > 0 ? fDepthEntries.Min() : 0;
            var eligible = new List<TryScope>();
            foreach (var s in scopes)
            {
                if (s.StartOffset < f && f - s.StartOffset < 600 && !s.FinallyHandler.HasValue && s.Depth == fDepth)
                {
                    var enclosingH = exceptTargets.Where(h => h < s.StartOffset && exceptHandlerEnds.TryGetValue(h, out var ehEnd) && s.StartOffset < ehEnd).ToList();
                    var encH = enclosingH.Count > 0 ? enclosingH[^1] : (int?)null;
                    if (!encH.HasValue)
                    {
                        eligible.Add(s);
                    }
                    else if (exceptHandlerEnds.TryGetValue(encH.Value, out var eEnd) && f < eEnd)
                    {
                        eligible.Add(s);
                    }
                }
            }
            if (eligible.Count == 0)
            {
                eligible = scopes
                    .Where(s => s.StartOffset < f && f - s.StartOffset < 600 && !s.FinallyHandler.HasValue && s.Depth == fDepth)
                    .ToList();
            }
            if (eligible.Count > 0)
            {
                var chosen = eligible.OrderByDescending(s => s.StartOffset).First();
                chosen.FinallyHandler = f;
                claimedTargets.Add(f);

                if (_offsetToIndex.TryGetValue(f, out var fIdx))
                {
                    if (fIdx > 0 && _instrs[fIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                    {
                        var target = _instrs[fIdx - 1].TargetOffset ?? 0;
                        if (target > chosen.EndOffset) chosen.EndOffset = target;
                    }
                    else
                    {
                        var cur = fIdx;
                        while (cur < _instrs.Count)
                        {
                            if (_instrs[cur].OpCode == OpCodes.JUMP_FORWARD)
                            {
                                var target = _instrs[cur].TargetOffset ?? 0;
                                if (target > chosen.EndOffset) chosen.EndOffset = target;
                                break;
                            }
                            else if (_instrs[cur].OpCode == OpCodes.RERAISE)
                            {
                                var after = _instrs[cur].Offset + 2;
                                if (after > chosen.EndOffset) chosen.EndOffset = after;
                                break;
                            }
                            cur++;
                        }
                    }
                }
            }
        }

        foreach (var pair in clusterPairs)
        {
            if (pair.Inner.FinallyHandler.HasValue && _offsetToIndex.TryGetValue(pair.Inner.FinallyHandler.Value, out var finHIdx))
            {
                if (finHIdx > 0 && _instrs[finHIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                {
                    var target = _instrs[finHIdx - 1].TargetOffset ?? 0;
                    if (target > pair.Inner.EndOffset)
                    {
                        pair.Inner.EndOffset = target;
                    }
                }
            }
            pair.Outer.BodyEndOffset = pair.Inner.EndOffset;
            if (pair.Outer.BodyEndOffset > pair.Outer.EndOffset)
            {
                pair.Outer.EndOffset = pair.Outer.BodyEndOffset;
            }
            if (pair.Outer.ExceptHandlers.Count > 0)
            {
                var firstH = pair.Outer.ExceptHandlers[0];
                if (_offsetToIndex.TryGetValue(firstH, out var hIdx) && _offsetToIndex.TryGetValue(pair.Outer.BodyEndOffset, out var bodyEndIdx))
                {
                    for (var k = hIdx - 1; k > bodyEndIdx; k--)
                    {
                        if (_instrs[k].OpCode == OpCodes.JUMP_FORWARD && (_instrs[k].TargetOffset ?? 0) > firstH && HasRealStatementsBetween(_instrs, bodyEndIdx, k))
                        {
                            pair.Outer.ElseStartOffset = pair.Outer.BodyEndOffset;
                            pair.Outer.ElseEndOffset = _instrs[k].Offset;
                            break;
                        }
                    }
                }
            }
        }

        scopes.RemoveAll(sc => sc.ExceptHandlers.Count == 0 && !sc.FinallyHandler.HasValue);

        foreach (var s in scopes)
        {
            if (s.FinallyHandler.HasValue && s.ExceptHandlers.Count == 0)
            {
                var inner = scopes.FirstOrDefault(other => other.StartOffset == s.BodyEndOffset && other != s);
                if (inner != null)
                {
                    if (inner.FinallyHandler.HasValue && _offsetToIndex.TryGetValue(inner.FinallyHandler.Value, out var ifHIdx))
                    {
                        if (ifHIdx > 0 && _instrs[ifHIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                        {
                            var target = _instrs[ifHIdx - 1].TargetOffset ?? 0;
                            if (target > inner.EndOffset) inner.EndOffset = target;
                        }
                    }
                    if (inner.EndOffset > s.EndOffset) s.EndOffset = inner.EndOffset;
                }
            }
        }


        foreach (var s in scopes)
        {
            s.ExceptHandlers.RemoveAll(h => !IsExceptHandlerEntry(h));
        }
        scopes.RemoveAll(sc => sc.ExceptHandlers.Count == 0 && !sc.FinallyHandler.HasValue);

        AddOrphanHandlerScopes(scopes, userEntries);

        _tryScopes.AddRange(scopes);
    }

    private bool IsExceptHandlerEntry(int offset)
    {
        if (!_offsetToIndex.TryGetValue(offset, out var idx)) return false;
        if (_instrs[idx].OpCode != OpCodes.PUSH_EXC_INFO) return false;
        if (idx + 1 < _instrs.Count && _instrs[idx + 1].OpCode == OpCodes.WITH_EXCEPT_START) return false;
        if (IsFinallyHandler(offset)) return false;
        return true;
    }

        private void AddOrphanHandlerScopes(List<TryScope> scopes, IReadOnlyList<ExceptionTableEntry> userEntries)
    {
        var entries = _code.ExceptionTable;
        if (entries.Count == 0) return;

        bool IsCovered(int off) => entries.Any(e => off >= e.Start && off < e.End);

        for (var hIdx = 0; hIdx < _instrs.Count; hIdx++)
        {
            var hIns = _instrs[hIdx];
            if (hIns.OpCode != OpCodes.PUSH_EXC_INFO) continue;
            var hOffset = hIns.Offset;


            if (userEntries.Any(e => e.Target == hOffset)) continue;
            if (!entries.Any(e => e.Start == hOffset)) continue;
            if (!IsExceptDispatchStart(hIdx)) continue;

            var tryStartIdx = FindTryStartIndex(hIdx, IsCovered);
            if (tryStartIdx < 0) continue;
            var tryStartOffset = _instrs[tryStartIdx].Offset;

            var bodyStartIdx = tryStartIdx;
            while (bodyStartIdx < hIdx && _instrs[bodyStartIdx].OpCode == OpCodes.NOP)
            {
                bodyStartIdx++;
            }
            if (bodyStartIdx >= hIdx) continue;


            scopes.RemoveAll(s => s.StartOffset >= tryStartOffset &&
                                  s.StartOffset < _instrs[bodyStartIdx].Offset &&
                                  s.ExceptHandlers.Count == 0);
            if (scopes.Any(s => s.StartOffset == tryStartOffset)) continue;

            var scope = new TryScope
            {
                StartOffset = tryStartOffset,
                BodyEndOffset = _instrs[bodyStartIdx].Offset,
                EndOffset = _instrs[bodyStartIdx].Offset,
                Depth = 0
            };
            scope.ExceptHandlers.Add(hOffset);

            var completion = FindHandlerCompletion(hIdx);


            var bodyJumpIdx = -1;
            for (var j = bodyStartIdx; j < hIdx; j++)
            {
                if (_instrs[j].OpCode == OpCodes.JUMP_FORWARD &&
                    (_instrs[j].TargetOffset ?? 0) > hOffset)
                {
                    bodyJumpIdx = j;
                    break;
                }
            }

            if (bodyJumpIdx == bodyStartIdx)
            {
                scope.BodyEndOffset = _instrs[bodyJumpIdx].Offset;
                var elseStart = _instrs[bodyJumpIdx].TargetOffset!.Value;
                if (completion.JumpTarget is int elseEnd && elseEnd > elseStart)
                {
                    scope.ElseStartOffset = elseStart;
                    scope.ElseEndOffset = elseEnd;
                    scope.EndOffset = elseEnd;
                }
                else
                {
                    scope.EndOffset = elseStart;
                }
                scopes.Add(scope);
                continue;
            }

            if (bodyJumpIdx > bodyStartIdx)
            {


                var elseStartOffset = _instrs[bodyStartIdx].Offset;
                var elseEndOffset = _instrs[bodyJumpIdx].Offset;
                var finTarget = entries
                    .Where(e => e.Start >= tryStartOffset && e.Start <= elseStartOffset &&
                                e.Target > hOffset && IsFinallyHandler(e.Target))
                    .Select(e => e.Target)
                    .FirstOrDefault();
                if (finTarget > 0)
                {
                    scope.ElseStartOffset = elseStartOffset;
                    scope.ElseEndOffset = elseEndOffset;
                    scope.FinallyHandler = finTarget;
                    scope.EndOffset = _instrs[bodyJumpIdx].TargetOffset!.Value;
                }
                else
                {
                    scope.BodyEndOffset = elseEndOffset;
                    scope.EndOffset = _instrs[bodyJumpIdx].TargetOffset!.Value;
                }
                scopes.Add(scope);
                continue;
            }


            var first = _instrs[bodyStartIdx];
            if (first.OpCode is OpCodes.RETURN_CONST or OpCodes.RETURN_VALUE or OpCodes.RAISE_VARARGS)
            {
                scope.BodyEndOffset = first.Offset;
                scope.EndOffset = completion.NextOffset > 0 ? completion.NextOffset : hOffset;
                scopes.Add(scope);
                continue;
            }


            if (completion.JumpTarget is int backTarget &&
                backTarget >= _instrs[bodyStartIdx].Offset && backTarget < hOffset &&
                _offsetToIndex.TryGetValue(backTarget, out var backIdx))
            {
                if (backIdx > bodyStartIdx)
                {
                    scope.ElseStartOffset = _instrs[bodyStartIdx].Offset;
                    scope.ElseEndOffset = backTarget;
                }
                scope.EndOffset = backTarget;
                scopes.Add(scope);
                continue;
            }

            var normalLen = hIdx - bodyStartIdx;
            var dupLen = 0;
            if (completion.NextIdx >= 0 && completion.NextIdx < _instrs.Count)
            {
                var max = Math.Min(normalLen, _instrs.Count - completion.NextIdx);
                for (var m = max; m >= 1; m--)
                {
                    var match = true;
                    for (var k = 0; k < m; k++)
                    {
                        var a = _instrs[hIdx - m + k];
                        var b = _instrs[completion.NextIdx + k];
                        if (a.OpCode != b.OpCode || !Equals(a.ArgVal, b.ArgVal))
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match)
                    {
                        dupLen = m;
                        break;
                    }
                }
            }

            var elseEndIdx = hIdx - dupLen;
            if (elseEndIdx > bodyStartIdx)
            {
                scope.ElseStartOffset = _instrs[bodyStartIdx].Offset;
                scope.ElseEndOffset = _instrs[elseEndIdx].Offset;
                scope.EndOffset = _instrs[elseEndIdx].Offset;
            }
            else
            {
                scope.EndOffset = _instrs[bodyStartIdx].Offset;
            }
            scopes.Add(scope);
        }
    }

    private bool IsExceptDispatchStart(int hIdx)
    {
        for (var k = hIdx + 1; k < _instrs.Count; k++)
        {
            var op = _instrs[k].OpCode;
            if (op == OpCodes.CHECK_EXC_MATCH || op == OpCodes.CHECK_EG_MATCH) return true;
            if (op is OpCodes.WITH_EXCEPT_START or OpCodes.PUSH_EXC_INFO or OpCodes.POP_EXCEPT or OpCodes.RERAISE) return false;
        }
        return false;
    }

    private int FindTryStartIndex(int hIdx, Func<int, bool> isCovered)
    {
        var hLine = _instrs[hIdx].StartsLine;
        for (var k = hIdx - 1; k >= 0; k--)
        {
            var ins = _instrs[k];
            if (ins.OpCode != OpCodes.NOP || isCovered(ins.Offset)) continue;
            if (hLine.HasValue && ins.StartsLine.HasValue && ins.StartsLine.Value > hLine.Value) continue;
            var runStart = k;
            while (runStart > 0 &&
                   _instrs[runStart - 1].OpCode == OpCodes.NOP &&
                   !isCovered(_instrs[runStart - 1].Offset))
            {
                runStart--;
            }
            return runStart;
        }
        return -1;
    }

    private readonly record struct HandlerCompletion(int NextIdx, int NextOffset, int? JumpTarget);

    private HandlerCompletion FindHandlerCompletion(int hIdx)
    {
        var nesting = 0;
        for (var k = hIdx + 1; k < _instrs.Count; k++)
        {
            var op = _instrs[k].OpCode;
            if (op == OpCodes.PUSH_EXC_INFO)
            {
                nesting++;
            }
            else if (op == OpCodes.POP_EXCEPT)
            {
                if (nesting > 0)
                {
                    nesting--;
                    continue;
                }
                var next = k + 1;
                while (next < _instrs.Count && _instrs[next].OpCode == OpCodes.EXTENDED_ARG) next++;
                if (next >= _instrs.Count) return new HandlerCompletion(-1, 0, null);
                if (_instrs[next].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                    _instrs[next].TargetOffset is int target)
                {
                    return new HandlerCompletion(next, _instrs[next].Offset, target);
                }
                if (_instrs[next].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RERAISE)
                {
                    return new HandlerCompletion(next, _instrs[next].Offset, null);
                }
                return new HandlerCompletion(next, _instrs[next].Offset, null);
            }
            else if (op == OpCodes.RERAISE && nesting == 0)
            {
                return new HandlerCompletion(-1, 0, null);
            }
        }
        return new HandlerCompletion(-1, 0, null);
    }

    private static bool BodyAlwaysTerminates(IReadOnlyList<StmtNode> body) =>
        body.Count > 0 && body[^1] is RaiseStmt or ReturnStmt;

        private List<StmtNode> SynthesizeDeadElse(List<ExceptClause> handlers)
    {
        var handlerNames = handlers
            .Select(h => h.VariableName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
        if (handlerNames.Count == 0) return [];

        var firstHandlerVarIdx = handlerNames
            .Select(n => IndexOfVar(n))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (firstHandlerVarIdx < 0) return [];

        var assigned = new List<(int Index, string Name)>();
        var seen = new HashSet<string>();
        foreach (var h in handlers)
        {
            foreach (var name in CollectAssignedNames(h.Body))
            {
                if (!seen.Add(name)) continue;
                var idx = IndexOfVar(name);
                if (idx >= 0 && idx < firstHandlerVarIdx)
                {
                    assigned.Add((idx, name));
                }
            }
        }

        assigned.Sort((a, b) => a.Index.CompareTo(b.Index));
        return assigned
            .Select(a => (StmtNode)new AssignStmt([new NameExpr(a.Name)], new ConstantExpr(null)))
            .ToList();
    }

    private int IndexOfVar(string name)
    {
        for (var i = 0; i < _code.VarNames.Count; i++)
        {
            if (_code.VarNames[i] == name) return i;
        }
        return -1;
    }

    private static IEnumerable<string> CollectAssignedNames(IReadOnlyList<StmtNode> stmts)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case AssignStmt a:
                    foreach (var t in a.Targets)
                    {
                        foreach (var n in CollectTargetNames(t)) yield return n;
                    }
                    break;
                case AugAssignStmt aug:
                    foreach (var n in CollectTargetNames(aug.Target)) yield return n;
                    break;
                case ForStmt f:
                    foreach (var n in CollectTargetNames(f.Target)) yield return n;
                    foreach (var n in CollectAssignedNames(f.Body)) yield return n;
                    foreach (var n in CollectAssignedNames(f.ElseBody)) yield return n;
                    break;
                case WhileStmt w:
                    foreach (var n in CollectAssignedNames(w.Body)) yield return n;
                    foreach (var n in CollectAssignedNames(w.ElseBody)) yield return n;
                    break;
                case IfStmt i:
                    foreach (var n in CollectAssignedNames(i.ThenBody)) yield return n;
                    foreach (var n in CollectAssignedNames(i.ElseBody)) yield return n;
                    break;
                case TryStmt t:
                    foreach (var n in CollectAssignedNames(t.Body)) yield return n;
                    foreach (var h in t.Handlers)
                    {
                        foreach (var n in CollectAssignedNames(h.Body)) yield return n;
                    }
                    foreach (var n in CollectAssignedNames(t.ElseBody)) yield return n;
                    if (t.FinallyBody != null)
                    {
                        foreach (var n in CollectAssignedNames(t.FinallyBody)) yield return n;
                    }
                    break;
                case WithStmt with:
                    foreach (var item in with.Items)
                    {
                        if (item.OptionalVars != null)
                        {
                            foreach (var n in CollectTargetNames(item.OptionalVars)) yield return n;
                        }
                    }
                    foreach (var n in CollectAssignedNames(with.Body)) yield return n;
                    break;
                case MatchStmt m:
                    foreach (var c in m.Cases)
                    {
                        foreach (var n in CollectAssignedNames(c.Body)) yield return n;
                    }
                    break;
            }
        }
    }

    private static IEnumerable<string> CollectTargetNames(ExprNode target)
    {
        switch (target)
        {
            case NameExpr { Name.Length: > 0 } name:
                yield return name.Name;
                break;
            case TupleExpr tuple:
                foreach (var e in tuple.Elements)
                {
                    foreach (var n in CollectTargetNames(e)) yield return n;
                }
                break;
            case ListExpr list:
                foreach (var e in list.Elements)
                {
                    foreach (var n in CollectTargetNames(e)) yield return n;
                }
                break;
            case StarredExpr star:
                foreach (var n in CollectTargetNames(star.Value)) yield return n;
                break;
        }
    }

        private bool IsCleanupOnlyEntry(ExceptionTableEntry e)
    {
        if (e.End != e.Target) return false;
        if (!_offsetToIndex.TryGetValue(e.Start, out var si)) return false;
        Span<int> seq = stackalloc int[3];
        var n = 0;
        for (var q = si; q < _instrs.Count && _instrs[q].Offset < e.End; q++)
        {
            var op = _instrs[q].OpCode;
            if (op == OpCodes.EXTENDED_ARG) continue;
            if (n >= 3) return false;
            seq[n++] = op;
        }
        return n == 3 && seq[0] == OpCodes.COPY && seq[1] == OpCodes.POP_EXCEPT && seq[2] == OpCodes.RERAISE;
    }

    private bool IsFinallyHandler(int targetOffset)
    {
        if (!_offsetToIndex.TryGetValue(targetOffset, out var tIdx)) return false;
        if (tIdx + 1 < _instrs.Count && _instrs[tIdx + 1].OpCode == OpCodes.POP_TOP) return false;
        var nextHandlerOffset = _code.ExceptionTable
            .Select(entry => entry.Target)
            .Where(target => target > targetOffset)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        var limit = _instrs.Count;
        if (nextHandlerOffset != int.MaxValue &&
            _offsetToIndex.TryGetValue(nextHandlerOffset, out var nextHandlerIdx))
        {
            limit = nextHandlerIdx;
        }
        for (var i = tIdx; i < limit; i++)
        {
            if (_instrs[i].OpCode == OpCodes.CHECK_EXC_MATCH) return false;
            if (_instrs[i].OpCode == OpCodes.POP_EXCEPT) return false;
            if (_instrs[i].OpCode == OpCodes.RERAISE && _instrs[i].OpArg == 0) return true;
        }
        return false;
    }

    public List<StmtNode> Decompile()
    {
        try
        {
            var stack = new SymbolicStack();
            var stmts = DecompileRange(0, _instrs.Count, stack);

            if (_code.FreeVars.Count > 0)
            {
                var modified = new HashSet<string>();
                foreach (var ins in _instrs)
                {
                    if (ins.OpCode == OpCodes.STORE_DEREF)
                    {
                        var vName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                        if (_code.FreeVars.Contains(vName))
                        {
                            modified.Add(vName);
                        }
                    }
                }
                if (modified.Count > 0)
                {
                    stmts.Insert(0, new NonlocalStmt(modified.OrderBy(v => v).ToList()));
                }
            }


            var globalModified = new HashSet<string>();
            foreach (var ins in _instrs)
            {
                if (ins.OpCode is OpCodes.STORE_GLOBAL or OpCodes.DELETE_GLOBAL)
                {
                    var name = ins.ArgRepr ?? ins.ArgVal?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        globalModified.Add(name);
                    }
                }
            }
            if (globalModified.Count > 0)
            {
                stmts.Insert(0, new GlobalStmt(globalModified.OrderBy(v => v).ToList()));
            }

            PreserveUnreachableElse(stmts);
            MergeTrailingConditionalReturn(stmts);
            AppendUnusedNameReferences(stmts);

            return [.. SyntheticCleanupPass.CleanStatements(stmts)];
        }
        catch (FailFastException ex)
        {
            throw new FailFastException($"Error in code object '{_code.Name}': {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error in code object '{_code.Name}': {ex.Message}", ex);
        }
    }

        private void MergeTrailingConditionalReturn(List<StmtNode> stmts)
    {


        if (IsPre312()) return;


        while (true)
        {
            if (stmts.Count < 2) return;
            if (stmts[^1] is not ReturnStmt { FromReturnConst: false } last) return;
            if (stmts[^2] is not IfStmt { ElseBody.Count: 0 } ifStmt) return;
            if (ifStmt.ThenBody.Count != 1 || ifStmt.ThenBody[0] is not ReturnStmt { FromReturnConst: false } thenRet) return;

            var ternary = new TernaryExpr(
                ifStmt.Condition,
                thenRet.Value ?? new ConstantExpr(null),
                last.Value ?? new ConstantExpr(null));
            stmts.RemoveRange(stmts.Count - 2, 2);
            stmts.Add(new ReturnStmt(ternary));
        }
    }

        private bool TrySkipLegacyInlineExitCleanup(ref int idx, int endIdx)
    {
        var p = SkipTransparent(idx + 1);
        if (p + 4 < endIdx && IsNoneConstant(_instrs[p]) &&
            _instrs[p + 1].OpCode == OpCodes.LEGACY_DUP_TOP &&
            _instrs[p + 2].OpCode == OpCodes.LEGACY_DUP_TOP &&
            _instrs[p + 3].OpCode == OpCodes.CALL &&
            (_instrs[p + 3].OpArg ?? 0) is 2 or 3 &&
            _instrs[p + 4].OpCode == OpCodes.POP_TOP)
        {
            p = SkipTransparent(p + 5);
            if (p < endIdx && _instrs[p].OpCode == OpCodes.POP_TOP) p++;
            p = SkipTransparent(p);
        }
        if (p < endIdx &&
            _instrs[p].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or
                OpCodes.JUMP_BACKWARD_NO_INTERRUPT or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
        {
            idx = p;
            return true;
        }
        return false;
    }

        private bool TrySkipModernInlineExitCleanup(ref int idx, int endIdx)
    {
        var p = idx;
        var matched = false;
        if (p + 3 < endIdx && IsNoneConstant(_instrs[p]) &&
            _instrs[p + 1].OpCode == OpCodes.LEGACY_DUP_TOP &&
            _instrs[p + 2].OpCode == OpCodes.LEGACY_DUP_TOP)
        {
            var callIdx = p + 3;
            while (callIdx < endIdx && _instrs[callIdx].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN or OpCodes.EXTENDED_ARG) callIdx++;
            if (callIdx + 1 < endIdx &&
                _instrs[callIdx].OpCode == OpCodes.CALL &&
                (_instrs[callIdx].OpArg ?? 0) is 2 or 3 &&
                _instrs[callIdx + 1].OpCode == OpCodes.POP_TOP)
            {
                p = SkipTransparent(callIdx + 2);
                matched = true;
            }
        }
        else if (p + 2 < endIdx && IsNoneConstant(_instrs[p]) &&
                 IsNoneConstant(_instrs[p + 1]) && IsNoneConstant(_instrs[p + 2]))
        {
            var callIdx = p + 3;
            while (callIdx < endIdx && _instrs[callIdx].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN or OpCodes.EXTENDED_ARG) callIdx++;
            if (callIdx < endIdx && _instrs[callIdx].OpCode == OpCodes.CALL &&
                (_instrs[callIdx].OpArg ?? 0) is 2 or 3)
            {
                p = SkipTransparent(callIdx + 1);
                matched = true;
            }
        }
        if (!matched) return false;
        p = SkipTransparent(p);
        while (p < endIdx && _instrs[p].OpCode == OpCodes.POP_TOP)
        {
            p++;
            p = SkipTransparent(p);
        }
        if (p < endIdx &&
            _instrs[p].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or
                OpCodes.JUMP_BACKWARD_NO_INTERRUPT or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
        {
            idx = p;
            return true;
        }


        if (p + 1 < endIdx && IsNoneConstant(_instrs[p]) && _instrs[p + 1].OpCode == OpCodes.RETURN_VALUE)
        {
            idx = p;
            return true;
        }
        return false;
    }

    private bool TryFindLegacyBlockPop(int bodyStart, int limit, out int popBlockIdx, bool preferLast = false)
    {
        var setupStack = new Stack<int>();
        var found = -1;
        for (var p = bodyStart; p < limit; p++)
        {
            var op = _instrs[p].OpCode;
            if (op is OpCodes.LEGACY_SETUP_FINALLY or OpCodes.LEGACY_SETUP_WITH)
            {
                setupStack.Push(p);
            }
            else if (op == OpCodes.LEGACY_POP_BLOCK)
            {
                if (setupStack.Count == 0)
                {
                    found = p;
                    if (!preferLast)
                    {
                        popBlockIdx = found;
                        return true;
                    }
                }
                else
                {
                    setupStack.Pop();
                }
            }
        }
        popBlockIdx = found;
        return found >= 0;
    }

        private bool TryDecompileLegacyTry(int idx, int endIdx, out StmtNode? tryStmt, out StmtNode? extraStmt, out int nextIdx)
    {
        tryStmt = null;
        extraStmt = null;
        nextIdx = idx;
        if (_instrs[idx].OpCode != OpCodes.LEGACY_SETUP_FINALLY) return false;

        var handlerOffset = _instrs[idx].TargetOffset ?? -1;
        if (!_offsetToIndex.TryGetValue(handlerOffset, out var handlerIdx)) return false;
        if (handlerIdx <= idx || handlerIdx >= endIdx) return false;

        TryFindLegacyBlockPop(idx + 1, handlerIdx, out var popBlockIdx, preferLast: true);
        if (popBlockIdx < 0)
        {


            for (var p = handlerIdx - 1; p > idx; p--)
            {
                if (_instrs[p].OpCode == OpCodes.LEGACY_POP_BLOCK)
                {
                    popBlockIdx = p;
                    break;
                }
            }
        }
        if (popBlockIdx < 0 && !LooksLikeExceptHandler(handlerIdx))
        {


            var abruptFinEnd = handlerIdx;
            while (abruptFinEnd < endIdx &&
                   _instrs[abruptFinEnd].OpCode is not (OpCodes.RERAISE or OpCodes.LEGACY_END_FINALLY))
            {
                abruptFinEnd++;
            }
            if (abruptFinEnd < endIdx)
            {
                var abruptFinBody = DecompileRange(handlerIdx, abruptFinEnd, new SymbolicStack());
                var tryBody = DecompileRange(idx + 1, handlerIdx, new SymbolicStack());
                tryStmt = new TryStmt(tryBody, [], [], abruptFinBody);
                nextIdx = abruptFinEnd + 1;
                return true;
            }
        }
        if (popBlockIdx < 0)
        {

            var hEnd = handlerIdx;
            while (hEnd < endIdx &&
                   _instrs[hEnd].OpCode is not (OpCodes.RERAISE or OpCodes.LEGACY_END_FINALLY))
            {
                hEnd++;
            }
            if (hEnd < endIdx) hEnd++;
            if (ParseLegacyExceptHandlers(handlerIdx, hEnd, out var abruptHandlers) && abruptHandlers.Count > 0)
            {
                var abruptBody = DecompileRange(idx + 1, handlerIdx, new SymbolicStack());
                tryStmt = new TryStmt(abruptBody, abruptHandlers, []);
                nextIdx = hEnd;
                return true;
            }
            return false;
        }

        var afterPop = popBlockIdx + 1;
        while (afterPop < handlerIdx && _instrs[afterPop].OpCode is OpCodes.NOP or OpCodes.EXTENDED_ARG) afterPop++;

        if (afterPop < handlerIdx && _instrs[afterPop].OpCode == OpCodes.JUMP_FORWARD)
        {
            var normalTargetOffset = _instrs[afterPop].TargetOffset ?? -1;
            if (!_offsetToIndex.TryGetValue(normalTargetOffset, out var normalTargetIdx)) return false;
            if (normalTargetIdx <= handlerIdx || normalTargetIdx > endIdx) return false;


            var dispatchEndIdx = -1;
            for (var q = handlerIdx; q < endIdx; q++)
            {
                if (_instrs[q].OpCode is OpCodes.RERAISE or OpCodes.LEGACY_END_FINALLY)
                {
                    dispatchEndIdx = q;
                    break;
                }
            }
            if (dispatchEndIdx < 0) dispatchEndIdx = normalTargetIdx;


            var handlerExitTargets = new HashSet<int>();


            var handlerScanEnd = Math.Max(dispatchEndIdx, normalTargetIdx);
            for (var q = handlerIdx; q < handlerScanEnd; q++)
            {
                if (_instrs[q].OpCode == OpCodes.JUMP_FORWARD &&
                    _instrs[q].TargetOffset is int exitTarget &&
                    exitTarget > normalTargetOffset)
                {
                    handlerExitTargets.Add(exitTarget);
                }
            }

            var handlerExitOffset = handlerExitTargets.Count > 0
                ? handlerExitTargets.Min()
                : normalTargetOffset;
            var hasElse = handlerExitTargets.Count > 0 && normalTargetOffset < handlerExitOffset;
            if (!_offsetToIndex.TryGetValue(handlerExitOffset, out var handlerExitIdx)) return false;
            var elseEndIdx = Math.Min(hasElse ? handlerExitIdx : normalTargetIdx, endIdx);

            if (!ParseLegacyExceptHandlers(handlerIdx, normalTargetIdx, out var handlers)) return false;
            if (handlers.Count == 0) return false;

            var body = DecompileRange(idx + 1, popBlockIdx, new SymbolicStack());
            var elseBody = hasElse ? DecompileRange(normalTargetIdx, elseEndIdx, new SymbolicStack()) : [];
            tryStmt = new TryStmt(body, handlers, elseBody);
            nextIdx = handlerExitIdx;
            return true;
        }


        if (afterPop < handlerIdx && LooksLikeExceptHandler(handlerIdx))
        {
            var handlerExit = -1;
            var fallHandlerReturns = false;
            for (var q = handlerIdx; q < endIdx; q++)
            {
                if (_instrs[q].OpCode == OpCodes.JUMP_FORWARD &&
                    _instrs[q].TargetOffset is int exitTarget &&
                    exitTarget > _instrs[handlerIdx].Offset &&
                    _offsetToIndex.TryGetValue(exitTarget, out var exitIdx))
                {
                    handlerExit = exitIdx;
                    break;
                }
                if (_instrs[q].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                {
                    handlerExit = q + 1;
                    fallHandlerReturns = true;
                    break;
                }
                if (_instrs[q].OpCode is OpCodes.RERAISE or OpCodes.LEGACY_END_FINALLY) break;
            }
            if (handlerExit > handlerIdx &&
                ParseLegacyExceptHandlers(handlerIdx, handlerExit, out var fallHandlers) && fallHandlers.Count > 0)
            {
                var tryBody = DecompileRange(idx + 1, popBlockIdx, new SymbolicStack());
                var elseBody = fallHandlerReturns
                    ? []
                    : DecompileRange(afterPop, handlerIdx, new SymbolicStack());
                tryStmt = new TryStmt(tryBody, fallHandlers, elseBody);
                nextIdx = handlerExit;
                return true;
            }
        }


        if (afterPop < handlerIdx && _instrs[afterPop].OpCode == OpCodes.LEGACY_CALL_FINALLY)
        {
            var endFin = handlerIdx;
            while (endFin < endIdx && _instrs[endFin].OpCode != OpCodes.LEGACY_END_FINALLY) endFin++;
            if (endFin < endIdx)
            {
                var tryStack = new SymbolicStack();
                var tryBody = DecompileRange(idx + 1, popBlockIdx, tryStack);
                if (tryStack.Count > 0)
                {
                    tryBody.Add(new ReturnStmt(tryStack.Pop()));
                }
                var callFinallyBody = DecompileRange(handlerIdx, endFin, new SymbolicStack());
                tryStmt = new TryStmt(tryBody, [], [], callFinallyBody);
                nextIdx = endFin + 1;
                return true;
            }
        }


        if (afterPop >= handlerIdx)
        {
            var endFin = handlerIdx;
            while (endFin < endIdx && _instrs[endFin].OpCode != OpCodes.LEGACY_END_FINALLY) endFin++;
            if (endFin < endIdx)
            {
                var sharedBody = DecompileRange(handlerIdx, endFin, new SymbolicStack());
                var sharedTry = DecompileRange(idx + 1, popBlockIdx, new SymbolicStack());
                tryStmt = new TryStmt(sharedTry, [], [], sharedBody);
                nextIdx = endFin + 1;
                return true;
            }
        }


        var finStart = afterPop;
        var finEnd = -1;
        var finNextIdx = endIdx;
        for (var q = finStart; q < endIdx; q++)
        {
            if (_instrs[q].OpCode == OpCodes.JUMP_FORWARD &&
                (_instrs[q].TargetOffset ?? -1) > handlerOffset)
            {
                finEnd = q;
                if (_offsetToIndex.TryGetValue(_instrs[q].TargetOffset ?? -1, out var jumpNextIdx)) finNextIdx = jumpNextIdx;
                break;
            }
            if (_instrs[q].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                _instrs[q].TargetOffset is int loopHeader &&
                IsActiveLoopHeader(loopHeader))
            {
                finEnd = q;
                finNextIdx = FindLegacyHandlerResume(handlerIdx, endIdx, loopHeader);
                break;
            }
            if (_instrs[q].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RAISE_VARARGS)
            {
                finEnd = q + 1;
                finNextIdx = endIdx;
                break;
            }
        }
        if (finEnd < 0) return false;


        var returnsAfterFinally = finEnd > finStart && _instrs[finEnd - 1].OpCode == OpCodes.RETURN_VALUE;
        var finBodyEnd = returnsAfterFinally ? finEnd - 1 : finEnd;
        var tryBodyStack = new SymbolicStack();
        var finTryBody = DecompileRange(idx + 1, popBlockIdx, tryBodyStack);
        if (returnsAfterFinally && tryBodyStack.Count > 0)
        {
            finTryBody.Add(new ReturnStmt(tryBodyStack.Pop()));
        }
        var finStack = new SymbolicStack();
        var finBody = DecompileRange(finStart, finBodyEnd, finStack);


        var handlerReturns = false;
        for (var q = handlerIdx; q < endIdx; q++)
        {
            if (_instrs[q].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                handlerReturns = true;
                break;
            }
            if (_instrs[q].OpCode is OpCodes.RERAISE or OpCodes.LEGACY_END_FINALLY) break;
        }

        StmtNode? trailingReturn = null;
        if (returnsAfterFinally && finStack.Count > 0 &&
            (finTryBody.Count == 0 || finTryBody[^1] is not ReturnStmt))
        {
            trailingReturn = new ReturnStmt(finStack.Pop());
        }
        if (!handlerReturns && finBody.Count > 0 && finBody[^1] is ReturnStmt { FromReturnConst: false })
        {
            trailingReturn = finBody[^1];
            finBody.RemoveAt(finBody.Count - 1);
        }

        if (finBody.Count > 0)
        {
            finTryBody = RemoveInlinedFinallyCopies(finTryBody, finBody);
        }


        if (finTryBody.Count == 1 && finTryBody[0] is TryStmt innerTry && innerTry.FinallyBody == null)
        {
            tryStmt = new TryStmt(innerTry.Body, innerTry.Handlers, innerTry.ElseBody, finBody);
        }
        else
        {
            tryStmt = new TryStmt(finTryBody, [], [], finBody);
        }
        extraStmt = trailingReturn;
        nextIdx = finNextIdx;
        return true;
    }

    private bool LooksLikeExceptHandler(int hIdx)
    {
        for (var q = hIdx; q < _instrs.Count; q++)
        {
            if (_instrs[q].OpCode is OpCodes.LEGACY_JUMP_IF_NOT_EXC_MATCH) return true;
            if (_instrs[q].OpCode == OpCodes.COMPARE_OP &&
                (_instrs[q].ArgRepr ?? _instrs[q].ArgVal?.ToString() ?? "") == "exception match")
                return true;
            if (_instrs[q].OpCode is OpCodes.POP_EXCEPT or OpCodes.RERAISE or OpCodes.LEGACY_END_FINALLY) return false;
        }
        return false;
    }

    private bool ParseLegacyExceptHandlers(int hStart, int hEnd, out List<ExceptClause> handlers)
    {
        handlers = new List<ExceptClause>();
        var h = hStart;
        while (h < hEnd)
        {
            while (h < hEnd && _instrs[h].OpCode is OpCodes.NOP or OpCodes.EXTENDED_ARG) h++;
            if (h >= hEnd) break;
            if (_instrs[h].OpCode is OpCodes.RERAISE or OpCodes.LEGACY_END_FINALLY) break;

            ExprNode? excType = null;
            string? varName = null;
            int unpackStart;
            var nextClauseOffset = -1;

            if (_instrs[h].OpCode == OpCodes.LEGACY_DUP_TOP)
            {
                var typeStart = h + 1;
                var checkIdx = -1;
                for (var q = typeStart; q < hEnd; q++)
                {
                    var op = _instrs[q].OpCode;
                    if (op == OpCodes.COMPARE_OP &&
                        (_instrs[q].ArgRepr ?? _instrs[q].ArgVal?.ToString() ?? "") == "exception match")
                    {
                        checkIdx = q;
                        break;
                    }
                    if (op == OpCodes.LEGACY_JUMP_IF_NOT_EXC_MATCH)
                    {
                        checkIdx = q;
                        break;
                    }
                    if (op is OpCodes.POP_TOP or OpCodes.STORE_FAST) break;
                }
                if (checkIdx < 0)
                {
                    return false;
                }

                excType = DecompileLegacyExpr(typeStart, checkIdx);
                if (excType == null)
                {
                    return false;
                }

                if (_instrs[checkIdx].OpCode == OpCodes.COMPARE_OP)
                {
                    var jumpPos = checkIdx + 1;
                    while (jumpPos < hEnd && _instrs[jumpPos].OpCode is OpCodes.EXTENDED_ARG or OpCodes.NOP or OpCodes.NOT_TAKEN)
                        jumpPos++;
                    if (jumpPos >= hEnd || _instrs[jumpPos].OpCode != OpCodes.POP_JUMP_IF_FALSE) return false;
                    nextClauseOffset = _instrs[jumpPos].TargetOffset ?? -1;
                    unpackStart = jumpPos + 1;
                }
                else
                {
                    nextClauseOffset = _instrs[checkIdx].TargetOffset ?? -1;
                    unpackStart = checkIdx + 1;
                }
            }
            else if (_instrs[h].OpCode == OpCodes.POP_TOP)
            {
                unpackStart = h;
            }
            else
            {
                return false;
            }

            var q2 = unpackStart;
            var popCount = 0;
            while (q2 < hEnd && _instrs[q2].OpCode == OpCodes.POP_TOP && popCount < 3)
            {
                popCount++;
                q2++;
            }
            if (popCount == 0)
            {
                return false;
            }
            if (popCount < 3 && q2 + 1 < hEnd &&
                _instrs[q2].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_DEREF)
            {
                varName = _instrs[q2].ArgRepr ?? _instrs[q2].ArgVal?.ToString();
                q2++;
                while (q2 < hEnd && _instrs[q2].OpCode == OpCodes.POP_TOP) q2++;
            }

            var bodyStart = q2;
            var cleanupSetupIdx = -1;
            if (bodyStart < hEnd && _instrs[bodyStart].OpCode == OpCodes.LEGACY_SETUP_FINALLY)
            {
                cleanupSetupIdx = bodyStart;
                bodyStart++;
            }

            var bodyEnd = -1;
            if (cleanupSetupIdx >= 0 &&
                _instrs[cleanupSetupIdx].TargetOffset is int cleanupTarget &&
                _offsetToIndex.TryGetValue(cleanupTarget, out var cleanupIdx) &&
                cleanupIdx > bodyStart && cleanupIdx < hEnd)
            {


                bodyEnd = cleanupIdx;
            }

            var depth = 0;
            for (var q = bodyStart; bodyEnd < 0 && q < hEnd; q++)
            {
                var op = _instrs[q].OpCode;
                if (op == OpCodes.LEGACY_SETUP_FINALLY)
                {
                    depth++;
                }
                else if (op == OpCodes.LEGACY_POP_BLOCK)
                {
                    if (depth > 0) depth--;
                }
                else if (op == OpCodes.POP_EXCEPT && depth == 0)
                {
                    bodyEnd = q;
                    break;
                }
                else if (op is OpCodes.RERAISE or OpCodes.LEGACY_END_FINALLY && depth == 0)
                {


                    var afterEnd = q + 1;
                    while (afterEnd < hEnd && _instrs[afterEnd].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) afterEnd++;
                    if (afterEnd < hEnd && _instrs[afterEnd].OpCode == OpCodes.POP_EXCEPT)
                    {
                        continue;
                    }
                    break;
                }
            }
            if (bodyEnd < 0 && hEnd > bodyStart && _instrs[hEnd - 1].OpCode == OpCodes.RERAISE)
            {
                bodyEnd = hEnd - 1;
            }
            if (bodyEnd < 0)
            {
                return false;
            }

            var clauseBody = DecompileRange(bodyStart, bodyEnd, new SymbolicStack());
            handlers.Add(new ExceptClause(excType, varName, clauseBody));

            if (nextClauseOffset >= 0)
            {
                if (!_offsetToIndex.TryGetValue(nextClauseOffset, out h)) return false;
            }
            else
            {
                h = bodyEnd + 1;
                while (h < hEnd && _instrs[h].OpCode is not (OpCodes.RERAISE or OpCodes.LEGACY_END_FINALLY)) h++;
            }
        }
        return handlers.Count > 0;
    }
    private ExprNode? DecompileLegacyExpr(int start, int end)
    {
        var exprStack = new SymbolicStack();
        for (var i = start; i < end; i++)
        {
            ExecuteInstruction(_instrs[i], exprStack, null);
        }
        return exprStack.Count > 0 ? exprStack.Pop() : null;
    }

        private bool TryDecompileLoadSpecialWith(int idx, int endIdx, SymbolicStack stack, out StmtNode? withStmt, out int nextIdx)
    {
        withStmt = null;
        nextIdx = idx;
        if (idx + 2 >= _instrs.Count) return false;
        if (_instrs[idx].OpCode != OpCodes.COPY || (_instrs[idx].OpArg ?? 0) != 1) return false;


        var p = idx + 1;
        while (p < _instrs.Count && _instrs[p].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) p++;
        if (p >= _instrs.Count || _instrs[p].OpCode != OpCodes.LOAD_SPECIAL) return false;
        var specialLoads = 0;
        while (p < _instrs.Count && _instrs[p].OpCode is OpCodes.LOAD_SPECIAL or OpCodes.SWAP)
        {
            if (_instrs[p].OpCode == OpCodes.LOAD_SPECIAL) specialLoads++;
            p++;
        }
        while (p < _instrs.Count && _instrs[p].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) p++;
        if (p >= _instrs.Count || _instrs[p].OpCode != OpCodes.CALL || (_instrs[p].OpArg ?? 0) != 0) return false;
        if (specialLoads < 2) return false;
        if (stack.Count == 0) return false;


        if (p + 1 < _instrs.Count && _instrs[p + 1].OpCode == OpCodes.GET_AWAITABLE) return false;

        var context = stack.Peek();
        var bodyStart = p + 1;
        ExprNode? target = null;
        if (bodyStart < _instrs.Count &&
            _instrs[bodyStart].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
        {
            target = new NameExpr(_instrs[bodyStart].ArgRepr ?? _instrs[bodyStart].ArgVal?.ToString() ?? "");
            bodyStart++;
        }

        var cleanupStart = -1;
        for (var q = bodyStart; q + 4 < endIdx; q++)
        {
            if (IsNoneConstant(_instrs[q]) &&
                IsNoneConstant(_instrs[q + 1]) &&
                IsNoneConstant(_instrs[q + 2]) &&
                _instrs[q + 3].OpCode == OpCodes.CALL && (_instrs[q + 3].OpArg ?? 0) == 3 &&
                _instrs[q + 4].OpCode == OpCodes.POP_TOP)
            {
                cleanupStart = q;
                break;
            }
        }
        if (cleanupStart < 0) return false;

        var body = DecompileRange(bodyStart, cleanupStart, new SymbolicStack());
        stack.Pop();
        withStmt = new WithStmt([new WithItem(context, target)], body);
        nextIdx = cleanupStart + 5;
        return true;
    }

    private static bool IsNoneConstant(PythonInstruction ins) =>
        ins.OpCode == OpCodes.LOAD_CONST &&
        (ins.ArgVal is null || ins.ArgRepr == "None");

        private bool TryDecompileLegacyWith(int idx, int endIdx, SymbolicStack stack, out StmtNode? withStmt, out int nextIdx)
    {
        withStmt = null;
        nextIdx = idx;
        if (_instrs[idx].OpCode != OpCodes.LEGACY_SETUP_WITH) return false;
        if (stack.Count == 0) return false;

        var context = stack.Peek();
        var handlerOffset = _instrs[idx].TargetOffset ?? -1;
        var limit = _offsetToIndex.TryGetValue(handlerOffset, out var handlerIdx) ? handlerIdx : endIdx;

        var bodyStart = idx + 1;
        while (bodyStart < limit && _instrs[bodyStart].OpCode is OpCodes.NOP or OpCodes.EXTENDED_ARG) bodyStart++;
        ExprNode? target = null;
        if (bodyStart < limit &&
            _instrs[bodyStart].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
        {
            target = new NameExpr(_instrs[bodyStart].ArgRepr ?? _instrs[bodyStart].ArgVal?.ToString() ?? "");
            bodyStart++;
        }
        else if (bodyStart < limit && _instrs[bodyStart].OpCode == OpCodes.POP_TOP)
        {
            bodyStart++;
        }

        TryFindLegacyBlockPop(bodyStart, limit, out var popBlockIdx, preferLast: true);
        if (popBlockIdx < 0)
        {


            var abruptBody = DecompileRange(bodyStart, limit, new SymbolicStack());
            var popExc = limit;
            while (popExc < endIdx && _instrs[popExc].OpCode != OpCodes.POP_EXCEPT) popExc++;
            var afterHandler = popExc + 1;
            while (afterHandler < endIdx && _instrs[afterHandler].OpCode is OpCodes.POP_TOP or OpCodes.NOP or OpCodes.NOT_TAKEN) afterHandler++;
            stack.Pop();
            withStmt = new WithStmt([new WithItem(context, target)], abruptBody);
            nextIdx = afterHandler;
            return true;
        }

        var bodyStack = new SymbolicStack();
        var body = DecompileRange(bodyStart, popBlockIdx, bodyStack);
        var bodyReturnIdx = -1;
        for (var q = popBlockIdx + 1; q < limit; q++)
        {
            if (_instrs[q].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                bodyReturnIdx = q;
                break;
            }
        }
        var bodyHasReturn = bodyReturnIdx >= 0 && bodyStack.Count > 0;
        if (bodyHasReturn)
        {
            body.Add(new ReturnStmt(bodyStack.Pop()));
        }
        if (bodyHasReturn)
        {
            _handlerStopIndices.Add(limit);
            stack.Pop();
            withStmt = new WithStmt([new WithItem(context, target)], body);
            nextIdx = bodyReturnIdx + 1;
            return true;
        }


        var next = popBlockIdx + 1;
        while (next < endIdx && _instrs[next].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN or OpCodes.EXTENDED_ARG) next++;

        if (next + 4 < _instrs.Count &&
            IsNoneConstant(_instrs[next]) &&
            _instrs[next + 1].OpCode == OpCodes.LEGACY_DUP_TOP &&
            _instrs[next + 2].OpCode == OpCodes.LEGACY_DUP_TOP &&
            _instrs[next + 3].OpCode == OpCodes.CALL && (_instrs[next + 3].OpArg ?? 0) == 3 &&
            _instrs[next + 4].OpCode == OpCodes.POP_TOP)
        {
            next += 5;
            while (next < endIdx && _instrs[next].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN or OpCodes.EXTENDED_ARG) next++;
            if (next < endIdx &&
                _instrs[next].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
            {
                var jumpTarget = _instrs[next].TargetOffset ?? -1;
                if (IsActiveLoopHeader(jumpTarget))
                {


                    _handlerStopIndices.Add(limit);
                    next = limit;
                }
                else if (_activeLoopExits.Contains(jumpTarget))
                {


                }
                else if (_offsetToIndex.TryGetValue(jumpTarget, out var jumpNext))
                {
                    next = jumpNext;
                }
            }
        }
        else
        {
            var endFinallyIdx = next;
            while (endFinallyIdx < endIdx && _instrs[endFinallyIdx].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) endFinallyIdx++;
            if (endFinallyIdx >= endIdx || _instrs[endFinallyIdx].OpCode != OpCodes.LEGACY_END_FINALLY) return false;
            next = endFinallyIdx + 1;
        }

        stack.Pop();
        withStmt = new WithStmt([new WithItem(context, target)], body);
        nextIdx = next;
        return true;
    }

    private List<StmtNode> DecompileRange(int startIdx, int endIdx, SymbolicStack stack)
    {
        var stmts = new List<StmtNode>();
        var idx = startIdx;

        while (idx < endIdx)
        {
            if (_handlerStopIndices.Contains(idx)) break;
            var ins = _instrs[idx];

            if (ins.OpCode == OpCodes.LEGACY_POP_BLOCK &&
                TrySkipLegacyInlineExitCleanup(ref idx, endIdx))
            {
                continue;
            }

            if (ins.OpCode == OpCodes.LOAD_CONST && IsNoneConstant(ins) &&
                TrySkipModernInlineExitCleanup(ref idx, endIdx))
            {
                continue;
            }

            if (ins.OpCode is OpCodes.PUSH_EXC_INFO or OpCodes.POP_EXCEPT or OpCodes.CHECK_EXC_MATCH) break;

            if (TryDecompileLegacyTry(idx, endIdx, out var legacyTryStmt, out var legacyExtraStmt, out var legacyTryNext))
            {
                if (legacyTryStmt != null) stmts.Add(legacyTryStmt);
                if (legacyExtraStmt != null) stmts.Add(legacyExtraStmt);
                idx = legacyTryNext;
                continue;
            }

            if (TryDecompileWith314(idx, endIdx, stack, out var with314Stmt, out var with314NextIdx))
            {
                stmts.Add(with314Stmt);
                idx = with314NextIdx;
                continue;
            }

            if (TryDecompileLoadSpecialWith(idx, endIdx, stack, out var specialWithStmt, out var specialWithNext))
            {
                if (specialWithStmt != null) stmts.Add(specialWithStmt);
                idx = specialWithNext;
                continue;
            }

            if (TryDecompileLegacyWith(idx, endIdx, stack, out var legacyWithStmt, out var legacyWithNext))
            {
                if (legacyWithStmt != null) stmts.Add(legacyWithStmt);
                idx = legacyWithNext;
                continue;
            }

            if (TryDecompileDeadTryFinally(idx, out var deadTryStmt, out var deadTryNext))
            {
                stmts.Add(deadTryStmt);
                idx = deadTryNext;
                continue;
            }

            if (ins.OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
            {
                var targetOffset = ins.TargetOffset ?? -1;
                if (_activeLoopExits.Contains(targetOffset))
                {
                    stmts.Add(new BreakStmt());
                    idx++;
                    continue;
                }
                if (IsActiveLoopHeader(targetOffset))
                {
                    stmts.Add(new ContinueStmt());
                    idx++;
                    continue;
                }
                if (ins.OpCode == OpCodes.JUMP_FORWARD && ins.TargetOffset.HasValue && _offsetToIndex.TryGetValue(ins.TargetOffset.Value, out var jumpTargetIdx) && jumpTargetIdx > idx)
                {
                    if (jumpTargetIdx <= endIdx)
                    {
                        idx = jumpTargetIdx;
                        continue;
                    }
                    break;
                }
            }

            if (TryDecompileImport(idx, stack, stmts, out var importNextIdx))
            {
                idx = importNextIdx;
                continue;
            }

            if (TryDecompileWith311(idx, endIdx, stack, out var with311Stmt, out var with311Next))
            {
                stmts.Add(with311Stmt);
                idx = with311Next;
                continue;
            }

            if (TryDecompileAsyncWith311(idx, endIdx, stack, out var asyncWith311Stmt, out var asyncWith311Next))
            {
                stmts.Add(asyncWith311Stmt);
                idx = asyncWith311Next;
                continue;
            }

            if (TryDecompileLegacyAsyncWith(idx, endIdx, stack, out var legacyAsyncWithStmt, out var legacyAsyncWithNext))
            {
                stmts.Add(legacyAsyncWithStmt);
                idx = legacyAsyncWithNext;
                continue;
            }

            if (TryDecompileWithStmt(idx, endIdx, stack, out var withStmt, out var withNextIdx))
            {
                stmts.Add(withStmt);
                idx = withNextIdx;
                continue;
            }

            if (TryDecompileAwaitOrYieldFrom(idx, stack, out var awaitYieldExpr, out var awaitNextIdx))
            {
                stack.Push(awaitYieldExpr);
                idx = awaitNextIdx;
                continue;
            }

            if (TryDecompileListGenexpr315(idx, endIdx, stack, out var listGenNext))
            {
                idx = listGenNext;
                continue;
            }

            if (TryDecompileSeqOrPatternLegacy(idx, endIdx, stack, out var seqOrStmt, out var seqOrNext))
            {
                stmts.Add(seqOrStmt);
                idx = seqOrNext;
                continue;
            }

            if (TryDecompileClassOrPatternLegacy(idx, endIdx, stack, out var classOrStmt, out var classOrNext))
            {
                stmts.Add(classOrStmt);
                idx = classOrNext;
                continue;
            }

            if (TryDecompileOrPatternLegacy(idx, endIdx, stack, out var orMatchStmt, out var orMatchNext))
            {
                stmts.Add(orMatchStmt);
                idx = orMatchNext;
                continue;
            }

            if (TryDecompileMatchStmt(idx, endIdx, stack, out var matchStmt, out var matchNextIdx))
            {
                stmts.Add(matchStmt);
                idx = matchNextIdx;
                continue;
            }


            if (TryDecompileInlinedComprehension(idx, stack, stmts, out var inlinedCompNextIdx))
            {
                idx = inlinedCompNextIdx;
                continue;
            }

            if (TryDecompileForLoop(idx, endIdx, stack, out var forStmt, out var forNextIdx))
            {
                stmts.Add(forStmt);
                idx = forNextIdx;
                continue;
            }

            if (TryDecompileLegacyAsyncForLoop(idx, endIdx, stack, out var legacyAsyncForStmt, out var legacyAsyncForNext))
            {
                stmts.Add(legacyAsyncForStmt);
                idx = legacyAsyncForNext;
                continue;
            }

            if (TryDecompileAsyncForLoop(idx, endIdx, stack, out var asyncForStmt, out var asyncForNextIdx))
            {
                stmts.Add(asyncForStmt);
                idx = asyncForNextIdx;
                continue;
            }

            if (TryDecompileWhileLoop(idx, endIdx, stack, out var whileStmt, out var whileNextIdx))
            {
                stmts.Add(whileStmt);
                idx = whileNextIdx;
                continue;
            }

            if (TryDecompileWhileTrueLoop(idx, endIdx, stack, out var whileTrueStmt, out var whileTrueNextIdx))
            {
                stmts.Add(whileTrueStmt);
                idx = whileTrueNextIdx;
                continue;
            }

            if (TryDecompileEmptyTryExceptElse(idx, endIdx, stack, out var emptyTryStmt, out var emptyTryNextIdx))
            {
                stmts.Add(emptyTryStmt);
                idx = emptyTryNextIdx;
                continue;
            }

            if (TryDecompileTryScope(idx, endIdx, stack, out var tryStmt, out var tryNextIdx))
            {
                stmts.Add(tryStmt);
                idx = tryNextIdx;
                continue;
            }

            if (TryDecompileSwapUnpackAssign(idx, stmts, out var swapUnpackNext))
            {
                idx = swapUnpackNext;
                continue;
            }

            if (TryDecompileUnpackAssign(idx, stack, stmts, out var unpackNextIdx))
            {
                idx = unpackNextIdx;
                continue;
            }

            if (TryDecompileMultiAssign(idx, stack, stmts, out var multiNextIdx))
            {
                idx = multiNextIdx;
                continue;
            }

            if (TryDecompileInlinedFinallyReturn(idx, stack, stmts, out var finRetNextIdx))
            {
                idx = finRetNextIdx;
                continue;
            }

            if (TryDecompileTernaryExpr(idx, endIdx, stack, out var ternaryEarlyNext))
            {
                idx = ternaryEarlyNext;
                continue;
            }

            if (TryDecompileShortCircuitBoolOp(ref idx, endIdx, stack))
            {
                idx++;
                continue;
            }

            if (TryDecompileAssert(idx, stack, stmts, out var assertNextIdx))
            {
                idx = assertNextIdx;
                continue;
            }

            if (TryDecompileChainedAssign(idx, stack, stmts, out var chainNextIdx))
            {
                idx = chainNextIdx;
                continue;
            }

            if (TryDecompileWalrus(ref idx, stack))
            {
                continue;
            }

            if (TryDecompileInlinedComprehension(idx, stack, stmts, out var compNextIdx))
            {
                idx = compNextIdx;
                continue;
            }

            if (TryDecompileDuplicatedTailConditional(idx, endIdx, stack, stmts, out var dupTailNextIdx))
            {
                idx = dupTailNextIdx;
                continue;
            }


            if (ins.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE &&
                idx + 1 < endIdx &&
                _instrs[idx + 1].OpCode == OpCodes.NOP &&
                !_instrs[idx + 1].StartsLine.HasValue)
            {
                var padBackIdx = SkipTransparent(idx + 1);
                if (padBackIdx < endIdx &&
                    _instrs[padBackIdx].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                    _instrs[padBackIdx].TargetOffset is int padBackTarget &&
                    _activeLoopHeaders.Contains(padBackTarget))
                {
                    idx = padBackIdx + 1;
                    continue;
                }
            }

            if (TryDecompileConditional(idx, endIdx, stack, out var ifStmt, out var ifNextIdx))
            {
                if (ifStmt != null)
                {
                    stmts.Add(ifStmt);
                }
                idx = ifNextIdx;
                continue;
            }
            if (TryDecompileChainedComparison(idx, stack, out var chainedExpr, out var ccNextIdx))
            {
                stack.Push(chainedExpr);
                idx = ccNextIdx;
                continue;
            }

            if (TryDecompileExceptReturn(idx, stack, stmts, out var retNextIdx))
            {
                idx = retNextIdx;
                continue;
            }

            try
            {
                ExecuteInstruction(ins, stack, stmts);
            }
            catch (FailFastException ex)
            {
                throw new FailFastException($"Error executing [{ins}] at index {idx}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error executing [{ins}] at index {idx}: {ex.Message}", ex);
            }

            if (ins.OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                var nextReachable = -1;
                for (var scan = idx + 1; scan < endIdx; scan++)
                {
                    if (_instrs[scan].IsJumpTarget && _instrs.Take(idx + 1).Any(i => (i.TargetOffset ?? -1) == _instrs[scan].Offset))
                    {
                        nextReachable = scan;
                        break;
                    }
                }
                if (nextReachable == -1)
                {
                    break;
                }
                idx = nextReachable;
                continue;
            }

            idx++;
        }

        return stmts;
    }

        private bool TryDecompileEmptyTryExceptElse(int idx, int endIdx, SymbolicStack stack, out StmtNode stmt, out int nextIdx)
    {
        stmt = null!;
        nextIdx = idx;
        if (idx + 2 >= _instrs.Count) return false;
        if (_instrs[idx].OpCode != OpCodes.NOP) return false;
        if (_instrs[idx + 1].OpCode != OpCodes.JUMP_FORWARD) return false;
        var elseOffset = _instrs[idx + 1].TargetOffset ?? -1;
        if (elseOffset <= _instrs[idx + 1].Offset) return false;
        if (!_offsetToIndex.TryGetValue(elseOffset, out var elseIdx) || elseIdx <= idx + 1) return false;

        var handlerIdx = idx + 2;
        while (handlerIdx < elseIdx && handlerIdx < _instrs.Count &&
               _instrs[handlerIdx].OpCode is OpCodes.NOP or OpCodes.EXTENDED_ARG)
        {
            handlerIdx++;
        }
        if (handlerIdx >= elseIdx || handlerIdx >= _instrs.Count) return false;
        if (_instrs[handlerIdx].OpCode != OpCodes.PUSH_EXC_INFO) return false;
        var handlerOffset = _instrs[handlerIdx].Offset;
        if (_code.ExceptionTable.Any(e => e.Target == handlerOffset)) return false;

        var hasExceptMatch = false;
        var exitOffset = -1;
        for (var i = handlerIdx + 1; i < elseIdx && i < _instrs.Count; i++)
        {
            if (_instrs[i].OpCode is OpCodes.CHECK_EXC_MATCH or OpCodes.CHECK_EG_MATCH) hasExceptMatch = true;
            if (hasExceptMatch && _instrs[i].OpCode == OpCodes.JUMP_FORWARD && i > handlerIdx &&
                _instrs[i - 1].OpCode == OpCodes.POP_EXCEPT &&
                (_instrs[i].TargetOffset ?? -1) >= elseOffset)
            {
                exitOffset = _instrs[i].TargetOffset ?? -1;
                break;
            }
        }
        if (!hasExceptMatch || exitOffset < 0) return false;
        if (!_offsetToIndex.TryGetValue(exitOffset, out var exitIdx)) return false;

        if (_tryScopes.Any(s => s.StartOffset == _instrs[idx].Offset && s.ExceptHandlers.Contains(handlerOffset)))
        {
            return false;
        }

        var synthetic = new TryScope
        {
            StartOffset = _instrs[idx].Offset,
            BodyEndOffset = elseOffset,
            EndOffset = exitOffset,
            ElseStartOffset = elseOffset,
            ElseEndOffset = exitOffset,
            Depth = 0
        };
        synthetic.ExceptHandlers.Add(handlerOffset);
        _tryScopes.Add(synthetic);

        if (!TryDecompileTryScope(idx, endIdx, stack, out stmt, out nextIdx))
        {
            _tryScopes.Remove(synthetic);
            return false;
        }
        return true;
    }

    private bool TryDecompileTryScope(int idx, int endIdx, SymbolicStack stack, out StmtNode tryStmt, out int nextIdx)
    {
        tryStmt = null!;
        nextIdx = idx;

        var ins = _instrs[idx];

        var matchingScopes = _tryScopes
            .Where(s => s.StartOffset == ins.Offset && !_activeScopes.Contains(s))
            .OrderByDescending(s => s.EndOffset - s.StartOffset)
            .ToList();

        if (matchingScopes.Count == 0) return false;

        var scope = matchingScopes[0];
        _activeScopes.Add(scope);

        try
        {
            if (!_offsetToIndex.TryGetValue(scope.BodyEndOffset, out var bodyEndIdx))
            {
                return false;
            }

            var bodyStartIdx = idx;
            if (_instrs[bodyStartIdx].OpCode == OpCodes.NOP)
            {
                bodyStartIdx++;
            }


            var bodyEndRawIdx = bodyEndIdx;
            while (bodyEndIdx < _instrs.Count && _instrs[bodyEndIdx].OpCode == OpCodes.EXTENDED_ARG)
            {
                bodyEndIdx++;
            }

            if (bodyEndIdx < _instrs.Count && _instrs[bodyEndIdx].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                bodyEndIdx++;
            }


            if (bodyEndIdx < _instrs.Count &&
                _instrs[bodyEndIdx].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
            {
                var bodyJumpTarget = _instrs[bodyEndIdx].TargetOffset ?? -1;
                if (_activeLoopExits.Contains(bodyJumpTarget) ||
                    (bodyEndRawIdx < bodyEndIdx && IsActiveLoopHeader(bodyJumpTarget)))
                {
                    bodyEndIdx++;
                }
            }


            if (scope.ExceptHandlers.Count == 0 && scope.FinallyHandler.HasValue &&
                _offsetToIndex.TryGetValue(scope.FinallyHandler.Value, out var finScanEnd) &&
                bodyStartIdx < bodyEndIdx && bodyEndIdx < _instrs.Count &&
                _instrs[bodyStartIdx].OpCode == OpCodes.JUMP_FORWARD &&
                (_instrs[bodyStartIdx].TargetOffset ?? -1) == _instrs[bodyEndRawIdx].Offset)
            {
                var scan = bodyStartIdx + 1;
                while (scan < bodyEndIdx && _instrs[scan].OpCode is OpCodes.NOP or OpCodes.EXTENDED_ARG) scan++;
                if (scan < bodyEndIdx && _instrs[scan].OpCode == OpCodes.PUSH_EXC_INFO &&
                    scan + 1 < bodyEndIdx &&
                    _instrs[scan + 1].OpCode is OpCodes.POP_TOP or OpCodes.BUILD_LIST or OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
                {
                    var handlerOff = _instrs[scan].Offset;
                    var hasExitJump = false;
                    for (var q = scan + 1; q < bodyEndIdx; q++)
                    {
                        if (_instrs[q].OpCode == OpCodes.RERAISE) break;
                        if (_instrs[q].OpCode == OpCodes.JUMP_FORWARD) { hasExitJump = true; break; }
                    }
                    if (hasExitJump && !_code.ExceptionTable.Any(e => e.Target == handlerOff))
                    {
                        scope.ExceptHandlers.Add(handlerOff);
                    }
                }
            }

            var bodyStack = stack.Clone();
            var tryBody = DecompileRange(bodyStartIdx, bodyEndIdx, bodyStack);

            var handlers = new List<ExceptClause>();
            foreach (var hOffset in scope.ExceptHandlers)
            {
                if (!_offsetToIndex.TryGetValue(hOffset, out var hIdx)) continue;
                if (hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.PUSH_EXC_INFO)
                {
                    hIdx++;
                }

                while (hIdx < _instrs.Count)
                {
                    while (hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.EXTENDED_ARG) hIdx++;
                    if (_instrs[hIdx].OpCode == OpCodes.BUILD_LIST && hIdx + 1 < _instrs.Count && _instrs[hIdx + 1].OpCode == OpCodes.COPY)
                    {
                        hIdx += 2;
                    }
                    while (hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.EXTENDED_ARG) hIdx++;
                    var matchIdx = hIdx;
                    while (matchIdx < _instrs.Count && _instrs[matchIdx].OpCode is not (OpCodes.CHECK_EXC_MATCH or OpCodes.CHECK_EG_MATCH or OpCodes.POP_EXCEPT or OpCodes.RERAISE))
                    {
                        if (_instrs[matchIdx].OpCode == OpCodes.POP_TOP && matchIdx == hIdx) break;
                        matchIdx++;
                    }

                    if (matchIdx < _instrs.Count && _instrs[matchIdx].OpCode is OpCodes.CHECK_EXC_MATCH or OpCodes.CHECK_EG_MATCH)
                    {
                        var isStar = _instrs[matchIdx].OpCode == OpCodes.CHECK_EG_MATCH;
                        var typeStack = new SymbolicStack();
                        DecompileRange(hIdx, matchIdx, typeStack);
                        var excType = typeStack.Count > 0 ? typeStack.Pop() : new NameExpr("Exception");
                        hIdx = matchIdx + 1;
                        while (hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.EXTENDED_ARG) hIdx++;
                        if (isStar && hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.COPY)
                        {
                            hIdx++;
                        }

                        while (hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.EXTENDED_ARG) hIdx++;
                        var nextClauseOffset = -1;
                        if (hIdx < _instrs.Count && _instrs[hIdx].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_NONE)
                        {
                            nextClauseOffset = _instrs[hIdx].TargetOffset ?? -1;
                            hIdx++;
                        }

                        while (hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.EXTENDED_ARG) hIdx++;


                        while (hIdx < _instrs.Count && _instrs[hIdx].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN or OpCodes.EXTENDED_ARG) hIdx++;
                        string? varName = null;
                        if (hIdx < _instrs.Count && _instrs[hIdx].OpCode is OpCodes.STORE_NAME or OpCodes.STORE_FAST)
                        {
                            varName = _instrs[hIdx].ArgRepr ?? _instrs[hIdx].ArgVal?.ToString();
                            hIdx++;
                        }
                        else if (hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.POP_TOP)
                        {
                            hIdx++;
                        }

                        var clauseEndIdx = hIdx;
                        if (isStar && nextClauseOffset >= 0 && _offsetToIndex.TryGetValue(nextClauseOffset, out var starLimitIdx))
                        {
                            var scan = starLimitIdx - 1;
                            while (scan >= hIdx && _instrs[scan].OpCode == OpCodes.EXTENDED_ARG) scan--;
                            if (scan >= hIdx && _instrs[scan].OpCode == OpCodes.JUMP_FORWARD)
                            {
                                scan--;
                                while (scan >= hIdx && _instrs[scan].OpCode == OpCodes.EXTENDED_ARG) scan--;
                                if (scan >= hIdx && _instrs[scan].OpCode == OpCodes.JUMP_FORWARD)
                                {
                                    scan--;
                                    while (scan >= hIdx && _instrs[scan].OpCode == OpCodes.EXTENDED_ARG) scan--;
                                }
                                if (scan >= hIdx && _instrs[scan].OpCode == OpCodes.POP_TOP)
                                {
                                    scan--;
                                    while (scan >= hIdx && _instrs[scan].OpCode == OpCodes.EXTENDED_ARG) scan--;
                                }
                                if (scan >= hIdx && _instrs[scan].OpCode == OpCodes.LIST_APPEND && _instrs[scan].OpArg == 3)
                                {
                                    scan--;
                                    while (scan >= hIdx && _instrs[scan].OpCode == OpCodes.EXTENDED_ARG) scan--;
                                }
                                if (scan >= hIdx && _instrs[scan].OpCode == OpCodes.JUMP_FORWARD)
                                {
                                    scan--;
                                    while (scan >= hIdx && _instrs[scan].OpCode == OpCodes.EXTENDED_ARG) scan--;
                                }
                            }
                            clauseEndIdx = scan + 1;
                        }
                        else if (nextClauseOffset >= 0 && _offsetToIndex.TryGetValue(nextClauseOffset, out var limitIdx))
                        {
                            var scan = limitIdx - 1;
                            while (scan >= hIdx && _instrs[scan].OpCode == OpCodes.EXTENDED_ARG) scan--;
                            if (scan >= hIdx && _instrs[scan].OpCode == OpCodes.RERAISE && _instrs[scan].OpArg == 1)
                            {
                                scan--;
                                while (scan >= hIdx && _instrs[scan].OpCode == OpCodes.EXTENDED_ARG) scan--;
                            }
                            if (scan >= hIdx && (_instrs[scan].OpCode is OpCodes.JUMP_FORWARD or OpCodes.RETURN_CONST or OpCodes.RETURN_VALUE))
                            {
                                var exitIdx = scan;
                                scan--;
                                while (scan >= hIdx && _instrs[scan].OpCode == OpCodes.EXTENDED_ARG) scan--;
                                clauseEndIdx = (scan >= hIdx && _instrs[scan].OpCode == OpCodes.POP_EXCEPT) ? scan : exitIdx;
                            }
                            else
                            {
                                clauseEndIdx = scan + 1;
                            }
                        }
                        else
                        {
                            var nesting = 0;
                            while (clauseEndIdx < _instrs.Count)
                            {
                                if (_instrs[clauseEndIdx].OpCode == OpCodes.PUSH_EXC_INFO)
                                {
                                    nesting++;
                                }
                                else if (_instrs[clauseEndIdx].OpCode == OpCodes.POP_EXCEPT)
                                {
                                    if (nesting == 0) break;
                                    nesting--;
                                }
                                else if (_instrs[clauseEndIdx].OpCode == OpCodes.RERAISE)
                                {
                                    if (nesting == 0) break;
                                }
                                clauseEndIdx++;
                            }
                        }

                        var clauseStack = stack.Clone();
                        var clauseBody = DecompileRange(hIdx, clauseEndIdx, clauseStack);
                        clauseBody = [.. SyntheticCleanupPass.CleanExceptionVariable(clauseBody, varName)];

                        if (clauseEndIdx < _instrs.Count && _instrs[clauseEndIdx].OpCode == OpCodes.POP_EXCEPT)
                        {
                            var afterPop = clauseEndIdx + 1;
                            var limitScan = nextClauseOffset >= 0 && _offsetToIndex.TryGetValue(nextClauseOffset, out var nxt) ? nxt : _instrs.Count;
                            while (afterPop < limitScan)
                            {
                                if (_instrs[afterPop].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                                {
                                    if (_code.Name != "<module>")
                                    {
                                        var retVal = clauseStack.Count > stack.Count ? clauseStack.Pop() : (_instrs[afterPop].OpCode == OpCodes.RETURN_CONST ? new ConstantExpr(_instrs[afterPop].ArgVal) : null);
                                        clauseBody.Add(new ReturnStmt(retVal));
                                    }
                                    break;
                                }
                                if (_instrs[afterPop].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
                                {
                                    var target = _instrs[afterPop].TargetOffset ?? -1;
                                    if (IsActiveLoopHeader(target))
                                    {
                                        clauseBody.Add(new ContinueStmt());
                                        break;
                                    }
                                }
                                if (_instrs[afterPop].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD)
                                {
                                    var target = _instrs[afterPop].TargetOffset ?? -1;
                                    if (_activeLoopExits.Contains(target))
                                    {
                                        clauseBody.Add(new BreakStmt());
                                        break;
                                    }
                                }
                                if (_instrs[afterPop].OpCode == OpCodes.RERAISE) break;
                                afterPop++;
                            }
                        }
                        else
                        {


                            var popExceptIdx = -1;
                            var popDepth = 0;
                            for (var q = hIdx; q < clauseEndIdx && q < _instrs.Count; q++)
                            {
                                var qOp = _instrs[q].OpCode;
                                if (qOp == OpCodes.PUSH_EXC_INFO) popDepth++;
                                else if (qOp == OpCodes.POP_EXCEPT)
                                {
                                    if (popDepth == 0) { popExceptIdx = q; break; }
                                    popDepth--;
                                }
                                else if (qOp == OpCodes.RERAISE && popDepth == 0) break;
                            }
                            if (popExceptIdx >= 0)
                            {
                                for (var q = popExceptIdx + 1; q < clauseEndIdx && q < _instrs.Count; q++)
                                {
                                    if (_instrs[q].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
                                    {
                                        var target = _instrs[q].TargetOffset ?? -1;
                                        if (IsActiveLoopHeader(target))
                                        {
                                            clauseBody.Add(new ContinueStmt());
                                            break;
                                        }
                                    }
                                    if (_instrs[q].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD)
                                    {
                                        var target = _instrs[q].TargetOffset ?? -1;
                                        if (_activeLoopExits.Contains(target))
                                        {
                                            clauseBody.Add(new BreakStmt());
                                            break;
                                        }
                                    }
                                    if (_instrs[q].OpCode == OpCodes.RERAISE) break;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(varName))
                        {
                            clauseBody = SyntheticCleanupPass.CleanExceptionVariable(clauseBody, varName).ToList();
                        }
                        handlers.Add(new ExceptClause(excType, varName, clauseBody, isStar));

                        if (nextClauseOffset >= 0 && _offsetToIndex.TryGetValue(nextClauseOffset, out var jumpNextIdx))
                        {
                            hIdx = jumpNextIdx;
                            while (hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.POP_TOP && hIdx + 1 < _instrs.Count && _instrs[hIdx + 1].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
                            {
                                hIdx++;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    else if (hIdx < _instrs.Count && _instrs[hIdx].OpCode == OpCodes.POP_TOP)
                    {
                        if (hIdx + 1 < _instrs.Count && _instrs[hIdx + 1].OpCode == OpCodes.LIST_APPEND)
                        {
                            break;
                        }
                        hIdx++;
                        var clauseEndIdx = hIdx;
                        var nesting = 0;
                        while (clauseEndIdx < _instrs.Count)
                        {
                            if (_instrs[clauseEndIdx].OpCode == OpCodes.PUSH_EXC_INFO)
                            {
                                nesting++;
                            }
                            else if (_instrs[clauseEndIdx].OpCode == OpCodes.POP_EXCEPT)
                            {
                                if (nesting == 0) break;
                                nesting--;
                            }
                            else if (_instrs[clauseEndIdx].OpCode == OpCodes.RERAISE)
                            {
                                if (nesting == 0) break;
                            }
                            clauseEndIdx++;
                        }

                        var clauseStack = stack.Clone();
                        var clauseBody = DecompileRange(hIdx, clauseEndIdx, clauseStack);
                        handlers.Add(new ExceptClause(null, null, clauseBody));
                        break;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            List<StmtNode>? finallyBody = null;
            var inlinedEndIdx = -1;
            var inlinedReturnIdx = -1;
            var hasFinInner = scope.ExceptHandlers.Count == 0 &&
                _tryScopes.Any(other => other.StartOffset == scope.BodyEndOffset && other != scope);

            if (hasFinInner && _offsetToIndex.TryGetValue(scope.BodyEndOffset, out var finStartIdx))
            {
                var finEndIdx = _offsetToIndex.TryGetValue(scope.EndOffset, out var feIdx) ? feIdx : endIdx;
                var fStack = stack.Clone();
                finallyBody = DecompileRange(finStartIdx, finEndIdx, fStack);
            }
            else if (scope.FinallyHandler.HasValue && _offsetToIndex.TryGetValue(scope.FinallyHandler.Value, out var finHIdx))
            {
                var inlinedStartIdx = -1;

                if (scope.ElseEndOffset.HasValue && _offsetToIndex.TryGetValue(scope.ElseEndOffset.Value, out var eEndIdx))
                {
                    var fStart = eEndIdx;
                    if (fStart < _instrs.Count && _instrs[fStart].OpCode == OpCodes.NOP) fStart++;
                    if (fStart < _instrs.Count && _instrs[fStart].OpCode == OpCodes.JUMP_FORWARD &&
                        _instrs[fStart].TargetOffset is int jTargetOffset &&
                        _offsetToIndex.TryGetValue(jTargetOffset, out var jTarget))
                    {
                        fStart = jTarget;
                    }

                    var fhStart = finHIdx;
                    if (fhStart < _instrs.Count && _instrs[fhStart].OpCode == OpCodes.PUSH_EXC_INFO) fhStart++;
                    var fhEnd = fhStart;
                    var nesting = 0;
                    var maxJump = fhStart;
                    while (fhEnd < _instrs.Count)
                    {
                        if (_instrs[fhEnd].TargetOffset is int targetOffset &&
                            _offsetToIndex.TryGetValue(targetOffset, out var tIdx) &&
                            tIdx > fhEnd &&
                            _instrs[fhEnd].OpCode != OpCodes.JUMP_BACKWARD)
                        {
                            if (tIdx > maxJump) maxJump = tIdx;
                        }

                        if (_instrs[fhEnd].OpCode == OpCodes.PUSH_EXC_INFO)
                        {
                            nesting++;
                        }
                        else if (_instrs[fhEnd].OpCode == OpCodes.POP_EXCEPT)
                        {
                            if (nesting > 0) nesting--;
                        }
                        else if (_instrs[fhEnd].OpCode is OpCodes.RERAISE or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                        {
                            if (nesting == 0 && fhEnd >= maxJump) break;
                        }
                        fhEnd++;
                    }
                    var finLen = fhEnd - fhStart;
                    if (finLen > 0 && fStart + finLen <= _instrs.Count)
                    {
                        var match = true;
                        for (var m = 0; m < finLen; m++)
                        {
                            if (fStart + m >= _instrs.Count ||
                                (_instrs[fStart + m].OpCode != _instrs[fhStart + m].OpCode &&
                                 !(_instrs[fStart + m].OpCode == OpCodes.JUMP_FORWARD && _instrs[fhStart + m].OpCode == OpCodes.RERAISE) &&
                                 !(m == finLen - 1 && _instrs[fStart + m].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST &&
                                   _instrs[fhStart + m].OpCode == OpCodes.RERAISE)))
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match)
                        {
                            inlinedStartIdx = fStart;
                            inlinedEndIdx = fStart + finLen;
                            if (finLen > 0 && _instrs[fhStart + finLen - 1].OpCode == OpCodes.RERAISE &&
                                _instrs[inlinedEndIdx - 1].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                            {
                                inlinedReturnIdx = inlinedEndIdx - 1;
                            }
                        }
                    }
                    if (inlinedStartIdx < 0)
                    {
                        var fEnd = fStart;
                        while (fEnd < _instrs.Count && fEnd < finHIdx)
                        {
                            if (_instrs[fEnd].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or
                                OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT or OpCodes.JUMP_FORWARD)
                            {
                                break;
                            }
                            fEnd++;
                        }
                        if (fEnd > fStart)
                        {
                            inlinedStartIdx = fStart;
                            inlinedEndIdx = fEnd;
                        }
                    }
                }
                else if (finHIdx > 0 && _instrs[finHIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                {
                    var jumpTarget = _instrs[finHIdx - 1].TargetOffset ?? 0;
                    if (jumpTarget > _instrs[finHIdx].Offset)
                    {
                        inlinedEndIdx = finHIdx - 1;

                        if (scope.ExceptHandlers.Count > 0)
                        {
                            var minScanIdx = _offsetToIndex.TryGetValue(scope.BodyEndOffset, out var bEnd) ? bEnd : 0;
                            for (var j = inlinedEndIdx - 1; j >= minScanIdx; j--)
                            {
                                if (_instrs[j].OpCode == OpCodes.RERAISE)
                                {
                                    inlinedStartIdx = j + 1;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            if (_offsetToIndex.TryGetValue(scope.BodyEndOffset, out var bEnd))
                            {
                                inlinedStartIdx = bEnd;
                            }
                        }

                        if (inlinedStartIdx >= 0 && inlinedStartIdx < _instrs.Count && _instrs[inlinedStartIdx].OpCode == OpCodes.NOP)
                        {
                            inlinedStartIdx++;
                        }
                    }
                }

                if (inlinedStartIdx < 0 && finHIdx < _instrs.Count)
                {
                    var fhStart = finHIdx;
                    if (fhStart < _instrs.Count && _instrs[fhStart].OpCode == OpCodes.PUSH_EXC_INFO) fhStart++;
                    var fhEnd = fhStart;
                    var nesting = 0;
                    var maxJump = fhStart;
                    while (fhEnd < _instrs.Count)
                    {
                        if (_instrs[fhEnd].TargetOffset is int targetOffset &&
                            _offsetToIndex.TryGetValue(targetOffset, out var tIdx) &&
                            tIdx > fhEnd &&
                            _instrs[fhEnd].OpCode != OpCodes.JUMP_BACKWARD)
                        {
                            if (tIdx > maxJump) maxJump = tIdx;
                        }

                        if (_instrs[fhEnd].OpCode == OpCodes.PUSH_EXC_INFO)
                        {
                            nesting++;
                        }
                        else if (_instrs[fhEnd].OpCode == OpCodes.POP_EXCEPT)
                        {
                            if (nesting > 0) nesting--;
                        }
                        else if (_instrs[fhEnd].OpCode is OpCodes.RERAISE or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                        {
                            if (nesting == 0 && fhEnd >= maxJump) break;
                        }
                        fhEnd++;
                    }
                    var finLen = fhEnd - fhStart;
                    if (finLen > 0)
                    {
                        var minScan = _offsetToIndex.TryGetValue(scope.BodyEndOffset, out var bEnd) ? bEnd : 0;


                        var maxScan = Math.Min(finHIdx - 1, _instrs.Count - finLen);
                        for (var scan = maxScan; scan >= minScan; scan--)
                        {
                            if (scan + finLen > finHIdx) continue;
                            var match = true;
                            for (var m = 0; m < finLen; m++)
                            {
                                var a = _instrs[scan + m];
                                var b = _instrs[fhStart + m];
                                if (a.OpCode != b.OpCode &&
                                    !(a.OpCode == OpCodes.JUMP_FORWARD && b.OpCode == OpCodes.RERAISE) &&
                                    !(m == finLen - 1 && a.OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST &&
                                      b.OpCode == OpCodes.RERAISE))
                                {
                                    match = false;
                                    break;
                                }


                                if (a.OpCode is not (OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT) &&
                                    !Equals(a.ArgVal, b.ArgVal))
                                {
                                    match = false;
                                    break;
                                }
                            }
                            if (match)
                            {
                                inlinedStartIdx = scan;
                                inlinedEndIdx = scan + finLen;
                                if (_instrs[fhStart + finLen - 1].OpCode == OpCodes.RERAISE &&
                                    _instrs[inlinedEndIdx - 1].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                                {
                                    inlinedReturnIdx = inlinedEndIdx - 1;
                                }
                                break;
                            }
                        }
                    }
                }

                if (inlinedStartIdx >= 0 && inlinedEndIdx > inlinedStartIdx)
                {
                    var fStack = stack.Clone();
                    var finallyRangeEnd = inlinedReturnIdx >= 0 ? inlinedReturnIdx : inlinedEndIdx;
                    finallyBody = DecompileRange(inlinedStartIdx, finallyRangeEnd, fStack);
                }
                else
                {
                    var fIdx = finHIdx;
                    if (fIdx < _instrs.Count && _instrs[fIdx].OpCode == OpCodes.PUSH_EXC_INFO)
                    {
                        fIdx++;
                    }

                    var fEndIdx = fIdx;
                    var nesting = 0;
                    var maxJump = fIdx;
                    while (fEndIdx < _instrs.Count)
                    {
                        if (_instrs[fEndIdx].TargetOffset is int targetOffset &&
                            _offsetToIndex.TryGetValue(targetOffset, out var tIdx) &&
                            tIdx > fEndIdx &&
                            _instrs[fEndIdx].OpCode != OpCodes.JUMP_BACKWARD)
                        {
                            if (tIdx > maxJump) maxJump = tIdx;
                        }

                        if (_instrs[fEndIdx].OpCode == OpCodes.PUSH_EXC_INFO)
                        {
                            nesting++;
                        }
                        else if (_instrs[fEndIdx].OpCode == OpCodes.POP_EXCEPT)
                        {
                            if (nesting > 0) nesting--;
                        }
                        else if (_instrs[fEndIdx].OpCode == OpCodes.RERAISE)
                        {
                            if (nesting == 0 && fEndIdx >= maxJump) break;
                        }
                        fEndIdx++;
                    }

                    var fStack = stack.Clone();
                    finallyBody = DecompileRange(fIdx, fEndIdx, fStack);
                }
            }


            var trailingReturnAfterFinally = finallyBody != null && inlinedEndIdx >= 0 &&
                inlinedEndIdx < _instrs.Count &&
                _instrs[inlinedEndIdx].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST;
            if (inlinedReturnIdx >= 0)
            {
                var retIns = _instrs[inlinedReturnIdx];
                if (retIns.OpCode == OpCodes.RETURN_CONST)
                {
                    tryBody.Add(new ReturnStmt(new ConstantExpr(retIns.ArgVal)));
                }
                else if (bodyStack.Count > 0)
                {
                    tryBody.Add(new ReturnStmt(bodyStack.Pop()));
                }
            }
            else if (trailingReturnAfterFinally && bodyStack.Count > stack.Count)
            {
                var retIns = _instrs[inlinedEndIdx];
                if (retIns.OpCode == OpCodes.RETURN_CONST)
                {
                    tryBody.Add(new ReturnStmt(new ConstantExpr(retIns.ArgVal)));
                }
                else
                {
                    tryBody.Add(new ReturnStmt(bodyStack.Pop()));
                }
                inlinedReturnIdx = inlinedEndIdx;
            }


            var tryReturnEpilogue = false;
            if (!scope.FinallyHandler.HasValue && scope.ExceptHandlers.Count > 0 &&
                scope.ElseStartOffset == scope.BodyEndOffset &&
                bodyStack.Count > stack.Count &&
                scope.ElseEndOffset.HasValue &&
                _offsetToIndex.TryGetValue(scope.ElseEndOffset.Value, out var epilogueEndIdx))
            {
                var sawPopExcept = false;
                for (var q = bodyEndIdx; q < epilogueEndIdx && q < _instrs.Count; q++)
                {
                    if (_instrs[q].OpCode == OpCodes.POP_EXCEPT)
                    {
                        sawPopExcept = true;
                    }
                    else if (sawPopExcept && _instrs[q].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                    {
                        tryReturnEpilogue = true;
                        break;
                    }
                }
            }
            if (tryReturnEpilogue)
            {
                tryBody.Add(new ReturnStmt(bodyStack.Pop()));
            }

            var elseBody = new List<StmtNode>();
            if (!tryReturnEpilogue && scope.ElseStartOffset.HasValue && scope.ElseEndOffset.HasValue &&
                _offsetToIndex.TryGetValue(scope.ElseStartOffset.Value, out var elseStartIdx) &&
                _offsetToIndex.TryGetValue(scope.ElseEndOffset.Value, out var elseEndIdx))
            {
                var elseStack = stack.Clone();
                elseBody = DecompileRange(elseStartIdx, elseEndIdx, elseStack);
            }


            if (scope.FinallyHandler.HasValue && inlinedEndIdx >= 0 && inlinedEndIdx < _instrs.Count &&
                _instrs[inlinedEndIdx].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST &&
                bodyStack.Count > stack.Count)
            {
                tryBody.Add(new ReturnStmt(bodyStack.Pop()));
            }


            if (!tryReturnEpilogue && elseBody.Count == 0 && handlers.Count > 0 && BodyAlwaysTerminates(tryBody))
            {
                elseBody = SynthesizeDeadElse(handlers);
            }

            if (!_offsetToIndex.TryGetValue(scope.EndOffset, out var scopeEndIdx))
            {
                scopeEndIdx = endIdx;
            }

            if (handlers.Count == 0 && finallyBody == null)
            {
                if (scope.ExceptHandlers.Count > 0)
                {
                    handlers.Add(new ExceptClause(null, null, []));
                }
                else
                {
                    return false;
                }
            }

            if (finallyBody != null && finallyBody.Count > 0 && _activeLoopHeaders.Count > 0)
            {
                ReplaceInlinedBreak(tryBody, finallyBody, bodyStartIdx, bodyEndIdx);
            }


            if (!tryReturnEpilogue && elseBody.Count == 0 && handlers.Count > 0 && tryBody.Count > 0 &&
                tryBody[^1] is RaiseStmt or ReturnStmt or BreakStmt or ContinueStmt)
            {
                foreach (var h in handlers)
                {
                    if (string.IsNullOrEmpty(h.VariableName)) continue;
                    var hvIdx = IndexInVarNames(h.VariableName!);
                    if (hvIdx < 0) continue;
                    string? pick = null;
                    var pickIdx = int.MaxValue;
                    foreach (var name in SimpleAssignedNames(h.Body))
                    {
                        var nIdx = IndexInVarNames(name);
                        if (nIdx >= 0 && nIdx < hvIdx && nIdx < pickIdx)
                        {
                            pick = name;
                            pickIdx = nIdx;
                        }
                    }
                    if (pick != null)
                    {
                        elseBody = [new AssignStmt([new NameExpr(pick)], new ConstantExpr(null))];
                        break;
                    }
                }
            }

            if (finallyBody is { Count: > 0 })
            {
                tryBody = RemoveInlinedFinallyCopies(tryBody, finallyBody);
            }

            tryStmt = new TryStmt(tryBody, handlers, elseBody, finallyBody);

            if (hasFinInner && _offsetToIndex.TryGetValue(scope.EndOffset, out var feNextIdx))
            {
                nextIdx = feNextIdx;
            }
            else if (scope.FinallyHandler.HasValue && _offsetToIndex.TryGetValue(scope.FinallyHandler.Value, out var finHIdx))
            {
                if (inlinedReturnIdx >= 0)
                {
                    nextIdx = Math.Min(FindFinallyHandlerEndIdx(finHIdx), endIdx);
                }
                else if (inlinedEndIdx >= 0 && inlinedEndIdx < finHIdx)
                {
                    if (_instrs[inlinedEndIdx].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                    {


                        nextIdx = inlinedEndIdx;
                    }
                    else if (_instrs[inlinedEndIdx].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
                    {
                        nextIdx = inlinedEndIdx + 1;
                    }
                    else if (_instrs[inlinedEndIdx].OpCode == OpCodes.JUMP_FORWARD)
                    {
                        nextIdx = _offsetToIndex.TryGetValue(_instrs[inlinedEndIdx].TargetOffset ?? 0, out var jIdx) ? jIdx : scopeEndIdx;
                    }
                    else
                    {
                        nextIdx = inlinedEndIdx;
                    }
                }
                else if (finHIdx > 0 && _instrs[finHIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                {
                    nextIdx = _offsetToIndex.TryGetValue(_instrs[finHIdx - 1].TargetOffset ?? 0, out var jIdx) ? jIdx : scopeEndIdx;
                }
                else if (finHIdx > 0 && _instrs[finHIdx - 1].OpCode == OpCodes.RAISE_VARARGS && _instrs[finHIdx - 1].Offset > scope.BodyEndOffset)
                {
                    nextIdx = finHIdx - 1;
                }
                else
                {
                    var curN = Math.Min(scopeEndIdx, endIdx);
                    if (finallyBody != null && finallyBody.Count > 0 &&
                        _offsetToIndex.TryGetValue(scope.BodyEndOffset, out var bEnd) &&
                        bEnd < _instrs.Count)
                    {
                        var fStart = bEnd;
                        while (fStart < _instrs.Count && _instrs[fStart].OpCode == OpCodes.NOP) fStart++;
                        var hStart = finHIdx + 1;
                        while (hStart < _instrs.Count && _instrs[hStart].OpCode == OpCodes.NOP) hStart++;
                        var matchCount = 0;
                        while (fStart + matchCount < _instrs.Count &&
                               hStart + matchCount < _instrs.Count &&
                               _instrs[hStart + matchCount].OpCode != OpCodes.RERAISE &&
                               _instrs[fStart + matchCount].OpCode == _instrs[hStart + matchCount].OpCode)
                        {
                            matchCount++;
                        }
                        if (matchCount > 0 && hStart + matchCount < _instrs.Count && _instrs[hStart + matchCount].OpCode == OpCodes.RERAISE)
                        {


                            var matchedEnd = fStart + matchCount;
                            var isPep709Restore = matchedEnd >= 1 &&
                                _instrs[matchedEnd - 1].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_DEREF &&
                                _clearedFastVars.Contains(_instrs[matchedEnd - 1].ArgRepr ?? _instrs[matchedEnd - 1].ArgVal?.ToString() ?? "");
                            if (!isPep709Restore)
                            {
                                curN = matchedEnd;
                            }
                        }
                    }
                    nextIdx = curN;
                }
            }
            else if (scope.PostElseResumeOffset.HasValue &&
                     _offsetToIndex.TryGetValue(scope.PostElseResumeOffset.Value, out var postElseIdx) &&
                     postElseIdx > idx && postElseIdx < _instrs.Count)
            {


                nextIdx = Math.Min(postElseIdx, endIdx);
            }
            else
            {
                nextIdx = Math.Min(scopeEndIdx, endIdx);
            }


            return true;
        }
        finally
        {
            _activeScopes.Remove(scope);
        }
    }

    private int SkipTransparent(int idx)
    {


        while (idx < _instrs.Count &&
               _instrs[idx].OpCode is OpCodes.NOP or OpCodes.EXTENDED_ARG or OpCodes.NOT_TAKEN &&
               !_instrs[idx].StartsLine.HasValue)
        {
            idx++;
        }
        return idx;
    }

    private bool MatchesInstructionSequence(int startA, int startB, int count)
    {
        if (startA < 0 || startB < 0) return false;
        var a = startA;
        var b = startB;
        for (var i = 0; i < count; i++)
        {
            a = SkipTransparent(a);
            b = SkipTransparent(b);
            if (a >= _instrs.Count || b >= _instrs.Count) return false;
            var insA = _instrs[a];
            var insB = _instrs[b];
            if (insA.OpCode != insB.OpCode) return false;
            if (insA.OpArg != insB.OpArg) return false;
            if (!Equals(insA.ArgVal, insB.ArgVal)) return false;
            a++;
            b++;
        }
        return true;
    }

    private static bool AreNodesEqual(AstNode? a, AstNode? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.GetType() != b.GetType()) return false;
        return a switch
        {
            ConstantExpr ca when b is ConstantExpr cb => Equals(ca.Value, cb.Value),
            NameExpr na when b is NameExpr nb => na.Name == nb.Name,
            UnaryExpr ua when b is UnaryExpr ub => ua.Op == ub.Op && AreNodesEqual(ua.Operand, ub.Operand),
            BinaryExpr ba when b is BinaryExpr bb => ba.Op == bb.Op && AreNodesEqual(ba.Left, bb.Left) && AreNodesEqual(ba.Right, bb.Right),
            TupleExpr ta when b is TupleExpr tb => ta.Elements.Count == tb.Elements.Count && ta.Elements.Zip(tb.Elements).All(p => AreNodesEqual(p.First, p.Second)),
            ReturnStmt ra when b is ReturnStmt rb => AreNodesEqual(ra.Value, rb.Value),
            AugAssignStmt aa when b is AugAssignStmt ab => aa.Op == ab.Op && AreNodesEqual(aa.Target, ab.Target) && AreNodesEqual(aa.Value, ab.Value),
            AssignStmt asgA when b is AssignStmt asgB => AreNodesEqual(asgA.Value, asgB.Value) && asgA.Targets.Count == asgB.Targets.Count && asgA.Targets.Zip(asgB.Targets).All(p => AreNodesEqual(p.First, p.Second)),
            ExprStmt ea when b is ExprStmt eb => AreNodesEqual(ea.Expression, eb.Expression),
            CallExpr ca when b is CallExpr cb => AreNodesEqual(ca.Function, cb.Function) && ca.Arguments.Count == cb.Arguments.Count && ca.Arguments.Zip(cb.Arguments).All(p => AreNodesEqual(p.First, p.Second)),
            CompareExpr cmpa when b is CompareExpr cmpb => AreNodesEqual(cmpa.Left, cmpb.Left) && cmpa.Comparators.Count == cmpb.Comparators.Count && cmpa.Comparators.Zip(cmpb.Comparators).All(p => p.First.Op == p.Second.Op && AreNodesEqual(p.First.Right, p.Second.Right)),
            BreakStmt => true,
            ContinueStmt => true,
            PassStmt => true,
            _ => Equals(a, b)
        };
    }

    private static bool CheckAndReplaceTrailingBreak(List<StmtNode> block, IReadOnlyList<StmtNode> finallyBody)
    {
        if (block.Count > 0 && block[^1] is ReturnStmt)
        {
            var finCount = finallyBody.Count;
            if (block.Count >= finCount + 1)
            {
                var match = true;
                for (var j = 0; j < finCount; j++)
                {
                    if (!AreNodesEqual(block[block.Count - 1 - finCount + j], finallyBody[j]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    block.RemoveRange(block.Count - 1 - finCount, finCount + 1);
                    block.Add(new BreakStmt());
                    return true;
                }
            }
        }
        return false;
    }

    private bool ReplaceInlinedBreak(List<StmtNode> stmts, IReadOnlyList<StmtNode> finallyBody, int bodyStartIdx, int bodyEndIdx)
    {
        var replaced = false;
        for (var i = 0; i < stmts.Count; i++)
        {
            if (stmts[i] is IfStmt ifStmt)
            {
                var thenList = new List<StmtNode>(ifStmt.ThenBody);
                if (CheckAndReplaceTrailingBreak(thenList, finallyBody))
                {
                    stmts[i] = new IfStmt(ifStmt.Condition, thenList, ifStmt.ElseBody);
                    replaced = true;
                }
                var elseList = new List<StmtNode>(ifStmt.ElseBody);
                if (CheckAndReplaceTrailingBreak(elseList, finallyBody))
                {
                    stmts[i] = new IfStmt(ifStmt.Condition, ifStmt.ThenBody, elseList);
                    replaced = true;
                }
            }
        }

        if (replaced)
        {
            var loopExit = _activeLoopExits.Count > 0 ? _activeLoopExits.Peek() : -1;
            if (loopExit >= 0 && _offsetToIndex.TryGetValue(loopExit, out var leIdx))
            {
                var endRet = -1;
                for (var scan = leIdx; scan < _instrs.Count; scan++)
                {
                    if (_instrs[scan].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                    {
                        endRet = scan;
                        break;
                    }
                }

                if (endRet >= 0)
                {
                    for (var scan = bodyEndIdx - 1; scan >= bodyStartIdx; scan--)
                    {
                        if (_instrs[scan].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                        {
                            var matchLen = 0;
                            while (scan - matchLen >= bodyStartIdx && endRet - matchLen >= leIdx)
                            {
                                var insA = _instrs[scan - matchLen];
                                var insB = _instrs[endRet - matchLen];
                                if (insA.OpCode != insB.OpCode || insA.OpArg != insB.OpArg || !Equals(insA.ArgVal, insB.ArgVal))
                                    break;
                                matchLen++;
                            }
                            if (matchLen > 0)
                            {
                                _lastInlinedExitStart = scan - matchLen + 1;
                                _lastInlinedExitLength = matchLen;
                            }
                            break;
                        }
                    }
                }
            }
        }

        return replaced;
    }

    private bool TryDecompileForLoop(int idx, int endIdx, SymbolicStack stack, out StmtNode forStmt, out int nextIdx)
    {
        forStmt = null!;
        nextIdx = idx;

        var ins = _instrs[idx];
        if (ins.OpCode != OpCodes.FOR_ITER) return false;

        var targetOffset = ins.TargetOffset ?? 0;
        if (!_offsetToIndex.TryGetValue(targetOffset, out var loopEndIdx)) return false;
        if (stack.Count == 0) return false;

        var iterator = stack.Pop();
        var bodyStartIdx = idx + 1;
        while (bodyStartIdx < endIdx && _instrs[bodyStartIdx].OpCode == OpCodes.EXTENDED_ARG)
        {
            bodyStartIdx++;
        }

        ExprNode target = new NameExpr("__pycrune_ph_item");
        var tScan = bodyStartIdx;
        if (TryParseTarget(ref tScan, out var parsedTarget) && tScan <= loopEndIdx)
        {
            target = parsedTarget;
            bodyStartIdx = tScan;
        }
        else
        {
            var targetStack = new SymbolicStack();
            targetStack.Push(new NameExpr("__pycrune_ph_for_target"));
            var targetStmts = new List<StmtNode>();
            var tIdx = bodyStartIdx;
            while (tIdx < loopEndIdx)
            {
                var tIns = _instrs[tIdx];
                ExecuteInstruction(tIns, targetStack, targetStmts);
                tIdx++;
                if (targetStack.Count == 0 && targetStmts.Count > 0)
                {
                    break;
                }
            }
            if (targetStmts.Count == 1 && targetStmts[0] is AssignStmt asgn1)
            {
                target = asgn1.Targets[0];
                bodyStartIdx = tIdx;
            }
            else if (targetStmts.Count > 1)
            {
                target = new TupleExpr(targetStmts.OfType<AssignStmt>().Select(a => a.Targets[0]).ToList());
                bodyStartIdx = tIdx;
            }
            else if (bodyStartIdx < endIdx && (_instrs[bodyStartIdx].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF))
            {
                var targetName = _instrs[bodyStartIdx].ArgRepr ?? _instrs[bodyStartIdx].ArgVal?.ToString() ?? "__pycrune_ph_item";
                target = new NameExpr(targetName);
                bodyStartIdx++;
            }
        }

        var loopBodyEnd = loopEndIdx;
        if (loopBodyEnd > 0 && _instrs[loopBodyEnd - 1].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
        {
            loopBodyEnd--;
        }

        var maxBreakTarget = -1;
        var nestedBreakTarget = -1;
        for (var scan = bodyStartIdx; scan < loopEndIdx; scan++)
        {
            var scanIns = _instrs[scan];
            if (scanIns.OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
            {
                var t = scanIns.TargetOffset ?? -1;
                if (t > targetOffset && t > maxBreakTarget)
                {
                    maxBreakTarget = t;
                }
                else if (scanIns.OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                         t >= 0 && t < ins.Offset && IsActiveLoopHeader(t) &&
                         t > nestedBreakTarget)
                {


                    nestedBreakTarget = t;
                }
            }
        }

        var loopExit = nestedBreakTarget > 0
            ? nestedBreakTarget
            : maxBreakTarget > targetOffset ? maxBreakTarget : targetOffset;
        _activeLoopHeaders.Push(ins.Offset);
        _activeLoopExits.Push(loopExit);

        List<StmtNode> body;
        try
        {
            var loopStack = stack.Clone();
            body = DecompileRange(bodyStartIdx, loopBodyEnd, loopStack);
        }
        finally
        {
            _activeLoopHeaders.Pop();
            _activeLoopExits.Pop();
        }

        body = TryMergeContinueGuards(body, bodyStartIdx, loopBodyEnd);

        var elseBody = new List<StmtNode>();
        nextIdx = loopEndIdx;

        if (nextIdx < endIdx && _instrs[nextIdx].OpCode == OpCodes.END_FOR)
        {
            nextIdx++;
        }

        if (nestedBreakTarget > 0)
        {


            var latchIdx = -1;
            for (var scan = nextIdx; scan < _instrs.Count; scan++)
            {
                var sIns = _instrs[scan];
                if (sIns.OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                    sIns.TargetOffset == nestedBreakTarget)
                {
                    latchIdx = scan;
                    break;
                }
            }
            if (latchIdx > nextIdx)
            {
                var elseStack = stack.Clone();
                elseBody = DecompileRange(nextIdx, latchIdx, elseStack);
                nextIdx = latchIdx;
            }
        }
        else if (maxBreakTarget > targetOffset && _offsetToIndex.TryGetValue(maxBreakTarget, out var elseEndIdx) && nextIdx < elseEndIdx)
        {
            var elseStack = stack.Clone();
            elseBody = DecompileRange(nextIdx, elseEndIdx, elseStack);
            nextIdx = elseEndIdx;
        }
        else if (_pendingBreakExitStart >= bodyStartIdx && _pendingBreakExitStart < loopEndIdx &&
                 _pendingBreakExitLength > 0)
        {


            var matchIdx = -1;
            for (var scan = nextIdx; scan + _pendingBreakExitLength <= _instrs.Count; scan++)
            {
                if (MatchesInstructionSequence(_pendingBreakExitStart, scan, _pendingBreakExitLength))
                {
                    matchIdx = scan;
                    break;
                }
            }
            if (matchIdx > nextIdx)
            {
                var elseStack = stack.Clone();
                elseBody = DecompileRange(nextIdx, matchIdx, elseStack);
                nextIdx = matchIdx;
            }
            _pendingBreakExitStart = -1;
            _pendingBreakExitLength = -1;
        }
        else if (_lastInlinedExitStart >= 0 && _lastInlinedExitLength > 0)
        {
            var matchIdx = -1;
            for (var scan = nextIdx; scan + _lastInlinedExitLength <= _instrs.Count; scan++)
            {
                if (MatchesInstructionSequence(_lastInlinedExitStart, scan, _lastInlinedExitLength))
                {
                    matchIdx = scan;
                    break;
                }
            }
            if (matchIdx > nextIdx)
            {
                var elseStack = stack.Clone();
                elseBody = DecompileRange(nextIdx, matchIdx, elseStack);
                nextIdx = matchIdx;
            }
            _lastInlinedExitStart = -1;
            _lastInlinedExitLength = -1;
        }

        forStmt = new ForStmt(target, iterator, body, elseBody);
        return true;
    }

        private List<StmtNode> TryMergeContinueGuards(List<StmtNode> body, int startIdx, int endIdx)
    {
        if (body.Count < 3 || startIdx >= endIdx || endIdx > _instrs.Count) return body;

        var boundaryCount = 0;
        foreach (var e in _code.ExceptionTable)
        {
            if (!e.LastI) continue;
            if (e.End <= _instrs[startIdx].Offset || e.End > _instrs[endIdx - 1].Offset + 2) continue;
            if (!_offsetToIndex.TryGetValue(e.End, out var bIdx)) continue;
            bIdx = SkipTransparent(bIdx);
            if (bIdx < _instrs.Count && _instrs[bIdx].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
            {
                boundaryCount++;
            }
        }
        if (boundaryCount < 2) return body;

        var chainStart = -1;
        var chainEnd = -1;
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i] is IfStmt { ElseBody.Count: 0 } s && s.ThenBody.Count == 1 && s.ThenBody[0] is ContinueStmt)
            {
                if (chainStart < 0)
                {
                    chainStart = i;
                }
            }
            else if (chainStart >= 0)
            {
                chainEnd = i;
                break;
            }
        }
        if (chainStart < 0) return body;
        if (chainEnd < 0)
        {
            chainEnd = body.Count;
        }
        if (chainEnd - chainStart < 2) return body;

        ExprNode? merged = null;
        for (var i = chainStart; i < chainEnd; i++)
        {
            var cond = InvertCondition(((IfStmt)body[i]).Condition);
            merged = merged == null ? cond : CombineAnd(merged, cond);
        }

        var rest = body.GetRange(chainEnd, body.Count - chainEnd);
        if (rest.Count == 0 || rest[^1] is not ContinueStmt)
        {
            rest.Add(new ContinueStmt());
        }

        var result = body.GetRange(0, chainStart);
        result.Add(new IfStmt(merged!, rest, []));
        return result;
    }

        private static void RemoveTrailingContinue(List<StmtNode> body)
    {
        if (body.Count > 0 && body[^1] is ContinueStmt) body.RemoveAt(body.Count - 1);
    }

    private int FindLegacyHandlerResume(int handlerIdx, int endIdx, int targetOffset)
    {
        for (var h = handlerIdx; h < endIdx; h++)
        {
            if (_instrs[h].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT or OpCodes.JUMP_FORWARD &&
                _instrs[h].TargetOffset == targetOffset)
            {
                return h + 1;
            }
            if (_instrs[h].OpCode == OpCodes.END_ASYNC_FOR) break;
        }
        return endIdx;
    }

    private int FindLegacyLoopBodyEnd(int bodyStart, int endAsyncIdx, int headerOffset)
    {
        var handlerLimit = endAsyncIdx;
        for (var k = bodyStart; k < endAsyncIdx; k++)
        {
            var op = _instrs[k].OpCode;
            if (op is not (OpCodes.LEGACY_SETUP_FINALLY or OpCodes.LEGACY_SETUP_WITH)) continue;
            if (_instrs[k].TargetOffset is not int target) continue;
            if (!_offsetToIndex.TryGetValue(target, out var targetIdx)) continue;
            if (targetIdx <= k || targetIdx >= endAsyncIdx) continue;
            if (_instrs[targetIdx].OpCode == OpCodes.END_ASYNC_FOR) continue;
            if (targetIdx < handlerLimit) handlerLimit = targetIdx;
        }

        for (var k = handlerLimit - 1; k >= bodyStart; k--)
        {
            if (_instrs[k].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                _instrs[k].TargetOffset == headerOffset)
            {
                return k;
            }
        }
        return endAsyncIdx;
    }


    private bool TryDecompileLegacyAsyncForLoop(int idx, int endIdx, SymbolicStack stack, out StmtNode asyncForStmt, out int nextIdx)
    {
        asyncForStmt = null!;
        nextIdx = idx;
        if (idx + 2 >= endIdx) return false;
        if (_instrs[idx].OpCode != OpCodes.GET_AITER) return false;
        var setupIdx = idx + 1;
        while (setupIdx < endIdx && _instrs[setupIdx].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) setupIdx++;
        if (setupIdx >= endIdx || _instrs[setupIdx].OpCode != OpCodes.LEGACY_SETUP_FINALLY) return false;
        var nextOpIdx = setupIdx + 1;
        while (nextOpIdx < endIdx && _instrs[nextOpIdx].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) nextOpIdx++;
        if (nextOpIdx >= endIdx || _instrs[nextOpIdx].OpCode != OpCodes.GET_ANEXT) return false;
        if (stack.Count == 0) return false;

        var iterator = stack.Peek();
        var headerOffset = _instrs[setupIdx].Offset;
        var bindIdx = nextOpIdx + 1;
        while (bindIdx < endIdx &&
               _instrs[bindIdx].OpCode is OpCodes.LOAD_CONST or OpCodes.LEGACY_YIELD_FROM or
                   OpCodes.LEGACY_POP_BLOCK or OpCodes.NOP or OpCodes.EXTENDED_ARG or OpCodes.NOT_TAKEN)
        {
            bindIdx++;
        }
        if (bindIdx >= endIdx ||
            _instrs[bindIdx].OpCode is not (OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF))
        {
            return false;
        }
        var targetName = _instrs[bindIdx].ArgRepr ?? _instrs[bindIdx].ArgVal?.ToString();
        if (string.IsNullOrEmpty(targetName))
        {
            throw new FailFastException("async for target name missing");
        }
        var target = new NameExpr(targetName);
        bindIdx++;


        if (_instrs[setupIdx].TargetOffset is not int endAsyncOffset ||
            !_offsetToIndex.TryGetValue(endAsyncOffset, out var endAsyncIdx) ||
            endAsyncIdx >= endIdx ||
            _instrs[endAsyncIdx].OpCode != OpCodes.END_ASYNC_FOR)
        {
            return false;
        }

        var loopBodyEnd = FindLegacyLoopBodyEnd(bindIdx, endAsyncIdx, headerOffset);
        var parseEnd = loopBodyEnd;
        for (var k = bindIdx; k < endAsyncIdx; k++)
        {
            if (_instrs[k].OpCode is not (OpCodes.LEGACY_SETUP_FINALLY or OpCodes.LEGACY_SETUP_WITH)) continue;
            if (_instrs[k].TargetOffset is not int handlerOffset) continue;
            if (!_offsetToIndex.TryGetValue(handlerOffset, out var handlerIdx)) continue;
            if (handlerIdx <= loopBodyEnd || handlerIdx >= endAsyncIdx) continue;
            if (_instrs[handlerIdx].OpCode == OpCodes.END_ASYNC_FOR) continue;
            parseEnd = endAsyncIdx;
            break;
        }

        _activeLoopHeaders.Push(headerOffset);
        List<StmtNode> body;
        try
        {
            body = DecompileRange(bindIdx, parseEnd, stack.Clone());
            RemoveTrailingContinue(body);
        }
        finally
        {
            _activeLoopHeaders.Pop();
        }

        stack.Pop();
        asyncForStmt = new ForStmt(target, iterator, body, [], IsAsync: true);
        nextIdx = endAsyncIdx + 1;
        return true;
    }

    private bool TryDecompileAsyncForLoop(int idx, int endIdx, SymbolicStack stack, out StmtNode asyncForStmt, out int nextIdx)
    {
        asyncForStmt = null!;
        nextIdx = idx;

        var ins = _instrs[idx];
        if (ins.OpCode != OpCodes.GET_AITER || idx + 1 >= endIdx || _instrs[idx + 1].OpCode != OpCodes.GET_ANEXT)
            return false;

        if (stack.Count == 0) return false;
        var iterator = stack.Pop();


        var sendIdx = idx + 1;
        while (sendIdx < endIdx && _instrs[sendIdx].OpCode is not (OpCodes.SEND or OpCodes.END_SEND)) sendIdx++;
        if (sendIdx >= endIdx) return false;
        int bindIdx;
        if (_instrs[sendIdx].OpCode == OpCodes.SEND &&
            _instrs[sendIdx].TargetOffset is int sendTarget &&
            _offsetToIndex.TryGetValue(sendTarget, out var sendTargetIdx))
        {
            bindIdx = sendTargetIdx;
            if (bindIdx < endIdx && _instrs[bindIdx].OpCode == OpCodes.END_SEND) bindIdx++;
        }
        else
        {
            while (sendIdx < endIdx && _instrs[sendIdx].OpCode != OpCodes.END_SEND) sendIdx++;
            if (sendIdx >= endIdx) return false;
            bindIdx = sendIdx + 1;
        }
        ExprNode? target = null;
        var targetStack = new SymbolicStack();
        targetStack.Push(new NameExpr("__pycrune_ph_for_target"));
        var targetStmts = new List<StmtNode>();
        var tIdx = bindIdx;
        while (tIdx < endIdx)
        {
            var tIns = _instrs[tIdx];
            ExecuteInstruction(tIns, targetStack, targetStmts);
            tIdx++;
            if (targetStack.Count == 0 && targetStmts.Count > 0)
            {
                break;
            }
        }
        if (targetStmts.Count == 1 && targetStmts[0] is AssignStmt asgn1)
        {
            target = asgn1.Targets[0];
            bindIdx = tIdx;
        }
        else if (targetStmts.Count > 1)
        {
            target = new TupleExpr(targetStmts.OfType<AssignStmt>().Select(a => a.Targets[0]).ToList());
            bindIdx = tIdx;
        }
        else if (bindIdx < endIdx && (_instrs[bindIdx].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF))
        {
            var targetName = _instrs[bindIdx].ArgRepr ?? _instrs[bindIdx].ArgVal?.ToString();
            if (string.IsNullOrEmpty(targetName)) return false;
            target = new NameExpr(targetName);
            bindIdx++;
        }
        if (target == null) return false;


        var sendOffset = _instrs[sendIdx].Offset;
        var endAsyncIdx = -1;
        foreach (var entry in _code.ExceptionTable)
        {
            if (sendOffset < entry.Start || sendOffset >= entry.End) continue;
            if (!_offsetToIndex.TryGetValue(entry.Target, out var handlerIdx)) continue;
            if (handlerIdx >= endIdx) continue;
            if (_instrs[handlerIdx].OpCode != OpCodes.END_ASYNC_FOR) continue;
            endAsyncIdx = handlerIdx;
            break;
        }
        if (endAsyncIdx < 0)
        {
            for (var scan = bindIdx; scan < endIdx; scan++)
            {
                if (_instrs[scan].OpCode == OpCodes.END_ASYNC_FOR)
                {
                    endAsyncIdx = scan;
                    break;
                }
            }
        }
        if (endAsyncIdx < 0) return false;

        var loopBodyEnd = endAsyncIdx;


        var scanLimit = endAsyncIdx;
        for (var k = bindIdx; k < endAsyncIdx; k++)
        {
            if (_instrs[k].OpCode == OpCodes.PUSH_EXC_INFO)
            {
                scanLimit = k;
                break;
            }
        }
        for (var k = scanLimit - 1; k >= bindIdx; k--)
        {
            if (_instrs[k].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT && _instrs[k].TargetOffset == _instrs[idx + 1].Offset)
            {
                loopBodyEnd = k;
                break;
            }
        }

        var loopStack = stack.Clone();
        _activeLoopHeaders.Push(_instrs[idx + 1].Offset);
        List<StmtNode> body;
        try
        {
            body = DecompileRange(bindIdx, loopBodyEnd, loopStack);
            RemoveTrailingContinue(body);
        }
        finally
        {
            _activeLoopHeaders.Pop();
        }
        nextIdx = endAsyncIdx + 1;
        asyncForStmt = new ForStmt(target, iterator, body, [], IsAsync: true);
        return true;
    }

    private bool TryDecompileWhileLoop(int idx, int endIdx, SymbolicStack stack, out StmtNode whileStmt, out int nextIdx)
    {
        whileStmt = null!;
        nextIdx = idx;

        var ins = _instrs[idx];
        if (ins.OpCode is not (OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE))
        {
            return false;
        }


        var exitTarget = ins.TargetOffset ?? 0;
        if (!_offsetToIndex.TryGetValue(exitTarget, out var exitIdx) || exitIdx <= idx)
        {
            return false;
        }

        var isRotated = false;
        var latchJumpIdx = -1;
        var bodyStartOffset = -1;
        var legacyCondLatchIdx = -1;
        for (var scan = exitIdx - 1; scan > idx; scan--)
        {
            var scanIns = _instrs[scan];
            if (scanIns.OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
            {
                var t = scanIns.TargetOffset ?? -1;
                if (IsActiveLoopHeader(t))
                {


                    continue;
                }
                if (t >= _instrs[idx + 1].Offset && t < scanIns.Offset)
                {


                    if (_offsetToIndex.TryGetValue(t, out var targetIdx))
                    {
                        while (targetIdx < _instrs.Count && _instrs[targetIdx].OpCode == OpCodes.EXTENDED_ARG)
                        {
                            targetIdx++;
                        }
                        if (targetIdx < _instrs.Count && _instrs[targetIdx].OpCode == OpCodes.FOR_ITER)
                            continue;
                    }
                    if (scan < exitIdx - 5)
                        continue;
                    var validRotatedCond = true;
                    if (_offsetToIndex.TryGetValue(t, out var bs))
                    {
                        for (var c = idx + 1; c < bs; c++)
                        {
                            if (_instrs[c].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
                            {
                                if (_instrs[c].TargetOffset != exitTarget) { validRotatedCond = false; break; }
                            }
                            if (_instrs[c].OpCode is OpCodes.FOR_ITER or OpCodes.PUSH_EXC_INFO) { validRotatedCond = false; break; }
                        }
                    }
                    if (!validRotatedCond) continue;
                    isRotated = true;
                    latchJumpIdx = scan;
                    bodyStartOffset = t;
                    break;
                }
                else if (t <= _instrs[idx].Offset && t > (_activeLoopHeaders.Count > 0 ? _activeLoopHeaders.Peek() : -1))
                {
                    latchJumpIdx = scan;
                    break;
                }
            }
            else if (scanIns.OpCode is OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_FALSE &&
                     scanIns.TargetOffset is int condBackTarget &&
                     condBackTarget >= _instrs[idx + 1].Offset && condBackTarget < scanIns.Offset &&
                     condBackTarget != exitTarget)
            {


                legacyCondLatchIdx = scan;
                break;
            }
        }

        if (latchJumpIdx == -1 && legacyCondLatchIdx >= 0)
        {
            var topCondStart = idx;
            while (topCondStart > 0 && IsConditionExpressionOpcode(_instrs[topCondStart - 1].OpCode)) topCondStart--;
            var condLen = idx - topCondStart;
            var bottomCondStart = legacyCondLatchIdx - condLen;
            if (condLen > 0 && bottomCondStart > idx + 1)
            {
                var sameCondition = true;
                for (var k = 0; k < condLen; k++)
                {
                    if (!SameConditionInstruction(_instrs[topCondStart + k], _instrs[bottomCondStart + k]))
                    {
                        sameCondition = false;
                        break;
                    }
                }
                if (sameCondition) latchJumpIdx = bottomCondStart;
            }
        }

        if (latchJumpIdx == -1) return false;

        var bodyStartIdx = isRotated && _offsetToIndex.TryGetValue(bodyStartOffset, out var bsIdx) ? bsIdx : idx + 1;
        var bodyEndIdx = latchJumpIdx;

        if (isRotated && bodyStartIdx > idx + 1)
        {
            var validRotated = true;
            for (var c = idx + 1; c < bodyStartIdx; c++)
            {
                if (_instrs[c].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
                {
                    if (_instrs[c].TargetOffset != exitTarget)
                    {
                        validRotated = false;
                        break;
                    }
                }
            }
            if (!validRotated)
            {
                return false;
            }
        }

        var cond = stack.Pop();
        if (ins.OpCode == OpCodes.POP_JUMP_IF_NONE)
        {
            cond = new CompareExpr(cond, [("is not", new ConstantExpr(null))]);
        }
        else if (ins.OpCode == OpCodes.POP_JUMP_IF_NOT_NONE)
        {
            cond = new CompareExpr(cond, [("is", new ConstantExpr(null))]);
        }
        else if (ins.OpCode == OpCodes.POP_JUMP_IF_TRUE)
        {
            cond = new UnaryExpr("not", cond);
        }

        if (isRotated && bodyStartIdx > idx + 1)
        {
            var cIdx = idx;
            while (cIdx < bodyStartIdx)
            {
                var branchIns = _instrs[cIdx];
                if (branchIns.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
                {
                    var nextBranchIdx = cIdx + 1;
                    while (nextBranchIdx < bodyStartIdx && _instrs[nextBranchIdx].OpCode is not (OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE))
                    {
                        nextBranchIdx++;
                    }
                    if (nextBranchIdx < bodyStartIdx)
                    {
                        var nextCondStack = new SymbolicStack();
                        DecompileRange(cIdx + 1, nextBranchIdx, nextCondStack);
                        if (nextCondStack.Count > 0)
                        {
                            var nextCond = nextCondStack.Pop();
                            var op = branchIns.OpCode == OpCodes.POP_JUMP_IF_FALSE ? "and" : "or";
                            cond = new BinaryExpr(cond, op, nextCond);
                        }
                        cIdx = nextBranchIdx;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    cIdx++;
                }
            }
        }

        if (isRotated)
        {
            var topCondStartIdx = idx;
            var condLine = _instrs[idx].StartsLine;
            if (condLine.HasValue)
            {
                while (topCondStartIdx > 0 && _instrs[topCondStartIdx - 1].StartsLine == condLine)
                {
                    topCondStartIdx--;
                }
            }
            var topCondLen = bodyStartIdx - topCondStartIdx;
            var latchCondEnd = latchJumpIdx;
            while (latchCondEnd > bodyStartIdx && _instrs[latchCondEnd - 1].OpCode == OpCodes.EXTENDED_ARG)
            {
                latchCondEnd--;
            }
            if (topCondLen > 0 && latchCondEnd - topCondLen >= bodyStartIdx)
            {
                bodyEndIdx = latchCondEnd - topCondLen;
            }
        }

        var maxBreakTarget = -1;
        for (var scan = bodyStartIdx; scan < latchJumpIdx; scan++)
        {
            var scanIns = _instrs[scan];
            if (scanIns.OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD)
            {
                var t = scanIns.TargetOffset ?? -1;
                if (t > exitTarget && t > maxBreakTarget)
                {
                    maxBreakTarget = t;
                }
            }
        }

        var loopCondOffset = _instrs[idx].Offset;
        for (var scanBack = idx - 1; scanBack >= 0; scanBack--)
        {
            if (_instrs[scanBack + 1].IsJumpTarget)
            {
                loopCondOffset = _instrs[scanBack + 1].Offset;
                break;
            }
        }

        var loopExit = maxBreakTarget > exitTarget ? maxBreakTarget : exitTarget;
        _activeLoopHeaders.Push(loopCondOffset);
        _activeLoopHeaders.Push(_instrs[idx].Offset);
        if (isRotated && bodyStartOffset >= 0)
        {
            _activeLoopHeaders.Push(bodyStartOffset);
        }
        _activeLoopExits.Push(loopExit);

        List<StmtNode> body;
        try
        {
            var loopStack = stack.Clone();
            body = DecompileRange(bodyStartIdx, bodyEndIdx, loopStack);
        }
        finally
        {
            if (isRotated && bodyStartOffset >= 0)
            {
                _activeLoopHeaders.Pop();
            }
            _activeLoopHeaders.Pop();
            _activeLoopHeaders.Pop();
            _activeLoopExits.Pop();
        }

        var elseBody = new List<StmtNode>();
        nextIdx = exitIdx;

        if (maxBreakTarget > exitTarget && _offsetToIndex.TryGetValue(maxBreakTarget, out var elseEndIdx) && nextIdx < elseEndIdx)
        {
            var elseStack = stack.Clone();
            elseBody = DecompileRange(nextIdx, elseEndIdx, elseStack);
            nextIdx = elseEndIdx;
        }
        else if (_pendingBreakExitStart >= bodyStartIdx && _pendingBreakExitStart < latchJumpIdx &&
                 _pendingBreakExitLength > 0)
        {


            var matchIdx = -1;
            for (var scan = nextIdx; scan + _pendingBreakExitLength <= _instrs.Count; scan++)
            {
                if (MatchesInstructionSequence(_pendingBreakExitStart, scan, _pendingBreakExitLength))
                {
                    matchIdx = scan;
                    break;
                }
            }
            if (matchIdx > nextIdx)
            {
                var elseStack = stack.Clone();
                elseBody = DecompileRange(nextIdx, matchIdx, elseStack);
                nextIdx = matchIdx;
            }
            _pendingBreakExitStart = -1;
            _pendingBreakExitLength = -1;
        }
        else if (_lastInlinedExitStart >= 0 && _lastInlinedExitLength > 0)
        {
            var matchIdx = -1;
            for (var scan = nextIdx; scan + _lastInlinedExitLength <= _instrs.Count; scan++)
            {
                if (MatchesInstructionSequence(_lastInlinedExitStart, scan, _lastInlinedExitLength))
                {
                    matchIdx = scan;
                    break;
                }
            }
            if (matchIdx > nextIdx)
            {
                var elseStack = stack.Clone();
                elseBody = DecompileRange(nextIdx, matchIdx, elseStack);
                nextIdx = matchIdx;
            }
            _lastInlinedExitStart = -1;
            _lastInlinedExitLength = -1;
        }


        if (body.Count > 0 && body[^1] is IfStmt { ElseBody.Count: 0 } latchIf &&
            latchIf.ThenBody.Count == 1 && latchIf.ThenBody[0] is ContinueStmt &&
            AreNodesEqual(latchIf.Condition, cond))
        {
            body.RemoveAt(body.Count - 1);
        }

        whileStmt = new WhileStmt(cond, body, elseBody);
        return true;
    }

    private bool TryDecompileWhileTrueLoop(int idx, int endIdx, SymbolicStack stack, out StmtNode whileStmt, out int nextIdx)
    {
        whileStmt = null!;
        nextIdx = idx;

        var ins = _instrs[idx];
        if (!ins.IsJumpTarget) return false;
        if (IsActiveLoopHeader(ins.Offset)) return false;


        var headerIdx = idx;
        while (headerIdx < _instrs.Count && _instrs[headerIdx].OpCode == OpCodes.EXTENDED_ARG)
        {
            headerIdx++;
        }
        var headerOp = headerIdx < _instrs.Count ? _instrs[headerIdx].OpCode : OpCodes.EXTENDED_ARG;
        if (headerOp is OpCodes.FOR_ITER or OpCodes.GET_AITER or OpCodes.GET_ANEXT or OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
        {
            return false;
        }

        var latchIdx = -1;
        for (var scan = idx + 1; scan < endIdx; scan++)
        {
            var sIns = _instrs[scan];
            if (sIns.OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT && sIns.TargetOffset == ins.Offset)
            {
                if (scan > 0 && _instrs[scan - 1].OpCode == OpCodes.POP_EXCEPT)
                    continue;

                var isExcHandler = false;
                for (var b = scan; b >= idx; b--)
                {
                    if (_instrs[b].OpCode == OpCodes.PUSH_EXC_INFO)
                    {
                        isExcHandler = true;
                        break;
                    }
                }
                if (isExcHandler) continue;

                latchIdx = scan;
                break;
            }
        }

        if (latchIdx == -1) return false;

        for (var c = idx; c < latchIdx; c++)
        {
            var cIns = _instrs[c];
            if (cIns.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
            {
                var target = cIns.TargetOffset ?? -1;
                if (target >= _instrs[latchIdx].Offset)
                {
                    return false;
                }
                break;
            }
            if (cIns.OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF or OpCodes.STORE_ATTR or OpCodes.STORE_SUBSCR or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RAISE_VARARGS)
            {


                var prev = c > idx ? _instrs[c - 1] : default;
                var isWalrus = (prev.OpCode == OpCodes.COPY && (prev.OpArg ?? 0) == 1) ||
                               prev.OpCode is OpCodes.LEGACY_DUP_TOP or OpCodes.LEGACY_DUP_TOP_TWO;
                if (isWalrus) continue;
                break;
            }
        }

        var maxBreakTarget = -1;
        for (var scan = idx; scan < latchIdx; scan++)
        {
            var sIns = _instrs[scan];
            if (sIns.OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD)
            {
                var t = sIns.TargetOffset ?? -1;
                if (t > _instrs[latchIdx].Offset && t > maxBreakTarget)
                {
                    maxBreakTarget = t;
                }
            }
        }

        _activeLoopHeaders.Push(ins.Offset);
        if (maxBreakTarget > 0)
        {
            _activeLoopExits.Push(maxBreakTarget);
        }

        List<StmtNode> body;
        try
        {
            var loopStack = stack.Clone();
            body = DecompileRange(idx, latchIdx, loopStack);
        }
        finally
        {
            _activeLoopHeaders.Pop();
            if (maxBreakTarget > 0)
            {
                _activeLoopExits.Pop();
            }
        }

        if (maxBreakTarget > 0 && _offsetToIndex.TryGetValue(maxBreakTarget, out var breakIdx))
        {
            nextIdx = breakIdx;
        }
        else
        {
            nextIdx = latchIdx + 1;
        }

        whileStmt = new WhileStmt(new ConstantExpr(true), body, []);
        return true;
    }

    private bool TryExtractMatchGuard(int startIdx, int limit, out ExprNode? guard, out int bodyIdx)
    {
        guard = null;
        bodyIdx = startIdx;
        var jumps = new List<ConditionJump>();
        var jumpStartToIndex = new Dictionary<int, int>();
        var condStack = new SymbolicStack();
        var i = startIdx;
        while (i < limit)
        {
            var ins = _instrs[i];
            if (ins.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
            {
                if (!TryGetBranchInfo(ins, condStack, out var cond, out var jit, out var target)) break;
                var fall = i + 1 < limit ? _instrs[i + 1].Offset : int.MaxValue;
                jumps.Add(new ConditionJump
                {
                    InstrIndex = i,
                    Condition = cond,
                    TrueOffset = jit ? target : fall,
                    FalseOffset = jit ? fall : target
                });
                jumpStartToIndex[ins.Offset] = jumps.Count - 1;
                i++;
                continue;
            }
            if (ins.OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_DEREF or OpCodes.STORE_GLOBAL
                or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD
                or OpCodes.POP_TOP or OpCodes.RAISE_VARARGS or OpCodes.RERAISE or OpCodes.NOP) break;
            try
            {
                ExecuteInstruction(ins, condStack, null);
            }
            catch (Exception ex) when (ex is not FailFastException)
            {
                break;
            }
            i++;
        }
        if (jumps.Count == 0) return false;


        for (var k = 0; k + 1 < jumps.Count; k++)
        {
            var fallIdx = jumps[k].InstrIndex + 1;
            if (fallIdx < _instrs.Count)
            {
                var fallOff = _instrs[fallIdx].Offset;
                if (!jumpStartToIndex.ContainsKey(fallOff))
                {
                    jumpStartToIndex[fallOff] = k + 1;
                }
            }
        }

        var bodyI = jumps[^1].InstrIndex + 1;
        var bodyOff = bodyI < _instrs.Count ? _instrs[bodyI].Offset : int.MaxValue;
        var failOff = -1;
        foreach (var j in jumps)
        {
            foreach (var t in new[] { j.TrueOffset, j.FalseOffset })
            {
                if (t != bodyOff && !jumpStartToIndex.ContainsKey(t) && t != int.MaxValue && t > failOff) failOff = t;
            }
        }
        if (failOff < 0) return false;
        var guardRegionStart = startIdx < _instrs.Count ? _instrs[startIdx].Offset : int.MaxValue;
        bool ValidGuardTarget(int t) =>
            t == failOff || t == bodyOff || jumpStartToIndex.ContainsKey(t) || t == int.MaxValue ||


            (t >= guardRegionStart && t < bodyOff);
        foreach (var j in jumps)
        {
            if (!ValidGuardTarget(j.TrueOffset)) return false;
            if (!ValidGuardTarget(j.FalseOffset)) return false;
        }
        guard = BuildConditionExpr(0, bodyOff, failOff, jumps, jumpStartToIndex);
        bodyIdx = bodyI;
        return true;
    }


    private sealed class PatParseException : Exception
    {
        public PatParseException() { }
        public PatParseException(string message) : base(message) { }
    }

    private static string RequirePatternName(PythonInstruction ins) =>
        ins.ArgRepr ?? ins.ArgVal?.ToString() ??
        throw new PatParseException($"missing name for {ins.OpName} at offset {ins.Offset}");

    private abstract record Pat2;
    private sealed record Pat2Val(ExprNode Value) : Pat2;
    private sealed record Pat2Wild : Pat2;
    private sealed record Pat2Cap(PatSlot Slot) : Pat2;
    private sealed record Pat2Star(PatSlot Slot) : Pat2;
    private sealed record Pat2Seq(IReadOnlyList<Pat2> Items, bool IsList) : Pat2;
    private sealed record Pat2Map(IReadOnlyList<(ExprNode Key, Pat2 Pat)> Entries, Pat2? Rest) : Pat2;
    private sealed record Pat2Class(ExprNode Cls, IReadOnlyList<Pat2> Args) : Pat2;
    private sealed record Pat2Kw(string Attr, Pat2 Pat) : Pat2;
    private sealed record Pat2Or(IReadOnlyList<Pat2Alt> Alts) : Pat2;
    private sealed record Pat2Alt(Pat2 Pat, IReadOnlyList<PatSlot> Caps);

    private sealed class PatSlot
    {
        public Pat2? Pat;
        public string? Name;
        public bool IsCapture;
        public bool IsStar;
        public bool IsPseudo;
        public bool IsKeys;
    }

    private sealed class PatCtx
    {
        public List<PatSlot> Stack { get; } = [];
        public int I;
        public List<int> FailTargets { get; } = [];
        public List<PatSlot> PendingPopped { get; } = [];
        public int CaseStart = -1;
        public int Depth;
    }

    private static PatSlot PatCloneSlot(PatSlot s) => new()
    {
        Pat = s.Pat,
        Name = s.Name,
        IsCapture = s.IsCapture,
        IsStar = s.IsStar,
        IsPseudo = s.IsPseudo,
        IsKeys = s.IsKeys
    };

    private static PatSlot PatPop(PatCtx c)
    {
        if (c.Stack.Count == 0) throw new PatParseException();
        var s = c.Stack[^1];
        c.Stack.RemoveAt(c.Stack.Count - 1);
        return s;
    }

    private static void PatSwap(PatCtx c, int n)
    {
        if (n < 2 || n > c.Stack.Count) throw new PatParseException($"swap {n} on depth {c.Stack.Count}");
        (c.Stack[^1], c.Stack[^n]) = (c.Stack[^n], c.Stack[^1]);
    }

    private static bool IsPatRotate(int op) =>
        op is OpCodes.LEGACY_ROT_TWO or OpCodes.LEGACY_ROT_THREE or OpCodes.LEGACY_ROT_FOUR or OpCodes.LEGACY_ROT_N;

    private static int PatRotateCount(PythonInstruction ins) => ins.OpCode switch
    {
        OpCodes.LEGACY_ROT_TWO => 2,
        OpCodes.LEGACY_ROT_THREE => 3,
        OpCodes.LEGACY_ROT_FOUR => 4,
        OpCodes.LEGACY_ROT_N => Math.Max(2, ins.OpArg ?? 2),
        _ => 2
    };


    private static void PatRotate(List<PatSlot> stack, int n)
    {
        if (n < 2 || n > stack.Count) throw new PatParseException($"rotate {n} on depth {stack.Count}");
        var top = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        stack.Insert(stack.Count - (n - 1), top);
    }

    private static void PatCopy(PatCtx c, int n)
    {
        if (n < 1 || n > c.Stack.Count) throw new PatParseException();
        c.Stack.Add(PatCloneSlot(c.Stack[^n]));
    }

    private static bool IsPatStoreOp(int op) =>
        op is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_DEREF or OpCodes.STORE_GLOBAL;

    private static string PatStoreName(PythonInstruction ins) =>
        ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "__pycrune_ph_x";


    private static int PatCmpIndex(PythonInstruction ins)
    {
        var repr = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
        if (repr.StartsWith("bool(", StringComparison.Ordinal) && repr.EndsWith(')'))
            repr = repr[5..^1];
        return repr switch
        {
            "<" => 0,
            "<=" => 1,
            "==" => 2,
            "!=" => 3,
            ">" => 4,
            ">=" => 5,
            _ => (ins.OpArg ?? 0) >> 4
        };
    }

    private static IEnumerable<object?> PatTupleItems(object? v)
    {
        if (v is null) yield break;
        if (v is string) { yield return v; yield break; }
        if (v is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var e in je.EnumerateArray())
                    yield return e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : e.ToString();
            }
            yield break;
        }
        if (v is System.Collections.IEnumerable en)
        {
            foreach (var o in en) yield return o;
        }
    }

    private void PatNoteFail(PatCtx c, PythonInstruction ins)
    {
        if (ins.TargetOffset is int t) c.FailTargets.Add(t);
    }

    private int PatNext(int i)
    {


        while (i < _instrs.Count && _instrs[i].OpCode is OpCodes.EXTENDED_ARG or OpCodes.NOT_TAKEN) i++;
        return i;
    }


    private void PatConsumeSwaps(PatCtx c, int limit)
    {
        c.I = PatNext(c.I);
        while (c.I < limit && _instrs[c.I].OpCode == OpCodes.SWAP)
        {
            PatSwap(c, _instrs[c.I].OpArg ?? 0);
            c.I = PatNext(c.I + 1);
        }
    }

    private PatternNode ResolvePat2(Pat2 p) => p switch
    {
        Pat2Val v => new MatchValuePattern(v.Value),
        Pat2Wild => new MatchWildcardPattern(),
        Pat2Cap cap => ResolvePat2Cap(cap.Slot),
        Pat2Star st => new MatchStarPattern(st.Slot.Name ?? "_"),
        Pat2Seq sq => new MatchSequencePattern(sq.Items.Select(ResolvePat2).ToList(), sq.IsList),
        Pat2Map mp => new MatchMappingPattern(
            mp.Entries.Select(e => (e.Key, ResolvePat2(e.Pat))).ToList(),
            mp.Rest == null ? null : ResolvePat2(mp.Rest)),
        Pat2Class cl => new MatchClassPattern(cl.Cls, cl.Args.Select(ResolvePat2).ToList()),
        Pat2Kw kw => new MatchKeywordPattern(kw.Attr, ResolvePat2(kw.Pat)),
        Pat2Or or => new MatchOrPattern(or.Alts.Select(a => ResolvePat2(a.Pat)).ToList()),
        _ => new MatchWildcardPattern()
    };

    private PatternNode ResolvePat2Cap(PatSlot s)
    {
        if (s.Name == null)
            return s.Pat == null ? new MatchWildcardPattern() : ResolvePat2(s.Pat);
        return new MatchAsPattern(s.Pat == null ? null : ResolvePat2(s.Pat), s.Name);
    }

    private void PropagateOrNames(Pat2 p)
    {
        switch (p)
        {
            case Pat2Or or:
                {
                    var first = or.Alts[0].Caps;
                    for (var ai = 1; ai < or.Alts.Count; ai++)
                    {
                        var caps = or.Alts[ai].Caps;
                        var n = Math.Min(first.Count, caps.Count);
                        for (var i = 0; i < n; i++)
                        {
                            if (caps[i].Name == null && first[i].Name != null)
                            {
                                caps[i].Name = first[i].Name;
                                caps[i].IsCapture = true;
                            }
                        }
                    }
                    foreach (var a in or.Alts) PropagateOrNames(a.Pat);
                    break;
                }
            case Pat2Seq sq:
                foreach (var x in sq.Items) PropagateOrNames(x);
                break;
            case Pat2Map mp:
                foreach (var e in mp.Entries) PropagateOrNames(e.Pat);
                if (mp.Rest != null) PropagateOrNames(mp.Rest);
                break;
            case Pat2Class cl:
                foreach (var x in cl.Args) PropagateOrNames(x);
                break;
            case Pat2Kw kw:
                PropagateOrNames(kw.Pat);
                break;
            case Pat2Cap cap:
                if (cap.Slot.Pat != null) PropagateOrNames(cap.Slot.Pat);
                break;
        }
    }

    private static List<PatSlot> PatCapsAbove(PatCtx c, int depth)
    {
        var caps = new List<PatSlot>();
        for (var i = c.Stack.Count - 1; i >= depth; i--) caps.Add(c.Stack[i]);
        return caps;
    }

    private static int PatIndexOfMax(PatCtx c)
    {
        var best = -1;
        var bestVal = int.MinValue;
        foreach (var t in c.FailTargets)
        {
            if (t > bestVal) { bestVal = t; best = t; }
        }
        return best;
    }

    private static bool PatTailMatches(IReadOnlyList<PythonInstruction> a, int aStart, int aEnd, IReadOnlyList<PythonInstruction> b, int bStart, int bEnd)
    {
        if (aEnd - aStart != bEnd - bStart) return false;
        for (var k = 0; k < aEnd - aStart; k++)
        {
            var x = a[aStart + k];
            var y = b[bStart + k];
            if (x.OpCode != y.OpCode) return false;
            if (x.OpArg != y.OpArg && !(x.OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE))
                return false;
            if (x.ArgVal is string sx && y.ArgVal is string sy && sx != sy) return false;
        }
        return true;
    }

    private Pat2 ParsePat(PatCtx c, PatSlot slot, int limit)
    {
        c.I = PatNext(c.I);


        if (c.I < limit && IsPatRotate(_instrs[c.I].OpCode) &&
            c.Stack.Count > 0 && !ReferenceEquals(c.Stack[^1], slot))
        {
            var trial = new List<PatSlot>(c.Stack);
            var p = c.I;
            try
            {
                while (p < limit && IsPatRotate(_instrs[p].OpCode))
                {
                    PatRotate(trial, PatRotateCount(_instrs[p]));
                    p = PatNext(p + 1);
                }
            }
            catch (PatParseException)
            {
                trial = null;
            }
            if (trial != null && trial.Count > 0 && ReferenceEquals(trial[^1], slot))
            {
                c.Stack.Clear();
                c.Stack.AddRange(trial);
                c.I = p;
            }
        }


        while (c.I < limit && _instrs[c.I].OpCode == OpCodes.SWAP)
        {
            var expect = _instrs[c.I].OpArg ?? 0;
            while (c.I < limit && _instrs[c.I].OpCode == OpCodes.SWAP && (_instrs[c.I].OpArg ?? 0) == expect && expect >= 2)
            {
                PatSwap(c, expect);
                expect--;
                c.I = PatNext(c.I + 1);
            }
            if (c.Stack.Count == 0 || !ReferenceEquals(c.Stack[^1], slot))
            {
                slot.IsCapture = true;
                return new Pat2Cap(slot);
            }
        }
        if (c.Stack.Count == 0 || !ReferenceEquals(c.Stack[^1], slot))
        {
            slot.IsCapture = true;
            return new Pat2Cap(slot);
        }
        c.I = PatNext(c.I);
        if (c.I >= limit) throw new PatParseException();

        var ins = _instrs[c.I];
        if (ins.OpCode == OpCodes.COPY && (ins.OpArg ?? 0) == 1)
            return ParseCopyPat(c, slot, limit);
        if (ins.OpCode == OpCodes.POP_TOP)
        {
            PatPop(c);
            c.I = PatNext(c.I + 1);
            return new Pat2Wild();
        }
        if (IsPatStoreOp(ins.OpCode))
        {
            var name = PatStoreName(ins);
            PatPop(c);
            c.I = PatNext(c.I + 1);
            slot.IsCapture = true;
            slot.Name = name;
            return new Pat2Cap(slot);
        }
        if (ins.OpCode == OpCodes.MATCH_SEQUENCE)
            return ParseSeqPat(c, slot, limit);
        if (ins.OpCode == OpCodes.MATCH_MAPPING)
            return ParseMapPat(c, slot, limit);
        if (ins.OpCode is OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
        {
            PatPop(c);
            PatNoteFail(c, ins);
            c.I = PatNext(c.I + 1);
            return new Pat2Val(new ConstantExpr(null));
        }
        if (ins.OpCode is OpCodes.LOAD_CONST or OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME or OpCodes.LOAD_FAST or OpCodes.LOAD_DEREF)
            return ParseValueOrClassPat(c, slot, limit);


        slot.IsCapture = true;
        return new Pat2Cap(slot);
    }

    private Pat2 ParseCopyPat(PatCtx c, PatSlot slot, int limit)
    {
        var baseStack = new List<PatSlot>(c.Stack);
        var failStart = c.FailTargets.Count;
        var copy = PatCloneSlot(slot);
        c.Stack.Add(copy);
        c.I = PatNext(c.I + 1);        var sub = ParsePat(c, copy, limit);        c.I = PatNext(c.I);
        while (c.I < limit && _instrs[c.I].OpCode == OpCodes.SWAP)
        {
            PatSwap(c, _instrs[c.I].OpArg ?? 0);
            c.I = PatNext(c.I + 1);
        }
        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.JUMP_FORWARD)
            return ParseOrPat(c, slot, baseStack, sub, limit, failStart, true);

        var altFails = new List<int>();
        for (var i = failStart; i < c.FailTargets.Count; i++) altFails.Add(c.FailTargets[i]);
        if (altFails.Count > 0 && TryParseDuplicatedTailOr(c, slot, baseStack, sub, limit, altFails, out var orPat))
            return orPat;

        slot.IsCapture = true;
        slot.Pat = sub;
        return new Pat2Cap(slot);
    }


    private bool TryParseDuplicatedTailOr(PatCtx c, PatSlot slot, List<PatSlot> baseStack, Pat2 firstAlt, int limit, List<int> firstFails, out Pat2 orPat)
    {
        orPat = null!;
        var firstFailOffset = firstFails.Max();
        if (!_offsetToIndex.TryGetValue(firstFailOffset, out var firstFailIdx)) return false;
        var tailStart = c.I;
        if (firstFailIdx <= tailStart) return false;        var alts = new List<Pat2Alt> { new(firstAlt, PatCapsAbove(c, baseStack.Count)) };
        var boundaries = new List<int>();
        var probe = firstFailIdx;
        while (true)
        {
            var p = probe;
            while (p < limit && _instrs[p].OpCode == OpCodes.POP_TOP) p++;
            if (p >= limit || _instrs[p].OpCode != OpCodes.COPY || (_instrs[p].OpArg ?? 0) != 1) break;

            var tmp = new PatCtx { I = p };
            foreach (var s in baseStack) tmp.Stack.Add(s);
            var copy = PatCloneSlot(slot);
            tmp.Stack.Add(copy);
            tmp.I = PatNext(tmp.I + 1);
            Pat2 alt;
            try
            {
                alt = ParsePat(tmp, copy, limit);
                while (tmp.I < limit && _instrs[tmp.I].OpCode == OpCodes.SWAP)
                {
                    PatSwap(tmp, _instrs[tmp.I].OpArg ?? 0);
                    tmp.I = PatNext(tmp.I + 1);
                }
            }
            catch (PatParseException)
            {
                return false;
            }

            var failOffset = PatIndexOfMax(tmp);
            if (failOffset < 0 || !_offsetToIndex.TryGetValue(failOffset, out var failIdx) || failIdx <= tmp.I)
                break;

            if (!PatTailMatches(_instrs, tailStart, firstFailIdx, _instrs, tmp.I, failIdx))
                break;

            alts.Add(new Pat2Alt(alt, PatCapsAbove(tmp, baseStack.Count)));
            boundaries.Add(failIdx);
            probe = failIdx;
        }

        if (alts.Count < 2) return false;


        var capCount = alts[0].Caps.Count;
        if (alts.Any(a => a.Caps.Count != capCount)) return false;

        foreach (var b in boundaries) c.FailTargets.Add(_instrs[b].Offset);
        orPat = new Pat2Or(alts);
        return true;
    }

    private Pat2 ParseOrPat(PatCtx c, PatSlot slot, List<PatSlot> baseStack, Pat2 firstAlt, int limit, int failStart, bool sharedEnd)
    {
        var firstFailOffset = c.FailTargets.Count > failStart ? c.FailTargets.Skip(failStart).Max() : -1;
        if (firstFailOffset < 0 || !_offsetToIndex.TryGetValue(firstFailOffset, out var probe))
            throw new PatParseException();
        var endOffset = _instrs[c.I].TargetOffset ?? -1;
        if (!_offsetToIndex.TryGetValue(endOffset, out var endIdx))
            throw new PatParseException();

        var alts = new List<Pat2Alt> { new(firstAlt, PatCapsAbove(c, baseStack.Count)) };
        var boundaries = new List<int>();
        var loop = 0;
        while (loop++ < 256)
        {
            var p = probe;
            while (p < limit && _instrs[p].OpCode == OpCodes.POP_TOP) p++;
            if (p >= limit) break;
            if (_instrs[p].OpCode == OpCodes.JUMP_FORWARD)
            {
                if (_instrs[p].TargetOffset is int nm) boundaries.Add(nm);
                break;
            }
            if (_instrs[p].OpCode != OpCodes.COPY || (_instrs[p].OpArg ?? 0) != 1) break;

            var tmp = new PatCtx { I = p };
            foreach (var s in baseStack) tmp.Stack.Add(s);
            var copy = PatCloneSlot(slot);
            tmp.Stack.Add(copy);
            tmp.I = PatNext(tmp.I + 1);
            Pat2 alt;
            try
            {
                alt = ParsePat(tmp, copy, limit);
                while (tmp.I < limit && _instrs[tmp.I].OpCode == OpCodes.SWAP)
                {
                    PatSwap(tmp, _instrs[tmp.I].OpArg ?? 0);
                    tmp.I = PatNext(tmp.I + 1);
                }
            }
            catch (PatParseException)
            {
                throw new PatParseException();
            }

            alts.Add(new Pat2Alt(alt, PatCapsAbove(tmp, baseStack.Count)));
            var altFailOffset = PatIndexOfMax(tmp);
            if (tmp.I < limit && _instrs[tmp.I].OpCode == OpCodes.JUMP_FORWARD)
            {
                if ((_instrs[tmp.I].TargetOffset ?? -1) != endOffset) throw new PatParseException();
                if (altFailOffset < 0 || !_offsetToIndex.TryGetValue(altFailOffset, out probe))
                    throw new PatParseException();
            }
            else
            {
                if (altFailOffset < 0) break;
                if (!_offsetToIndex.TryGetValue(altFailOffset, out probe)) throw new PatParseException();
            }
        }

        var capCount = alts[0].Caps.Count;
        if (alts.Any(a => a.Caps.Count != capCount)) throw new PatParseException();

        c.I = endIdx;        foreach (var b in boundaries) c.FailTargets.Add(b);
        return new Pat2Or(alts);
    }

    private Pat2 ParseSeqPat(PatCtx c, PatSlot slot, int limit)
    {
        c.I = PatNext(c.I);
        PatNoteFail(c, _instrs[c.I]);
        c.I = PatNext(c.I + 1);
        var flag = new PatSlot { IsPseudo = true };
        c.Stack.Add(flag);
        if (c.I < limit && _instrs[c.I].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
        {
            PatNoteFail(c, _instrs[c.I]);
            PatPop(c);
            c.I = PatNext(c.I + 1);
        }

        var size = -1;
        var hasStar = false;
        if (c.I + 3 < limit &&
            _instrs[c.I].OpCode == OpCodes.GET_LEN &&
            _instrs[c.I + 1].OpCode == OpCodes.LOAD_CONST &&
            _instrs[c.I + 2].OpCode == OpCodes.COMPARE_OP &&
            _instrs[c.I + 3].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
        {
            var n = Convert.ToInt32(_instrs[c.I + 1].ArgVal ?? 0);
            var cmp = PatCmpIndex(_instrs[c.I + 2]);
            if (cmp == 2) { size = n; hasStar = false; }
            else if (cmp == 5) { size = n + 1; hasStar = true; }
            else throw new PatParseException();
            PatNoteFail(c, _instrs[c.I + 3]);
            c.I = PatNext(c.I + 4);
        }

        if (c.Stack.Count == 0 || !ReferenceEquals(c.Stack[^1], slot)) throw new PatParseException();

        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.POP_TOP)
        {
            c.I = PatNext(c.I + 1);
            PatPop(c);
            var n = size >= 0 ? size : 1;
            var items = new List<Pat2>();
            for (var i = 0; i < n; i++)
            {
                if (hasStar && i == n - 1)
                {
                    var st = new PatSlot { IsCapture = true, IsStar = true };
                    items.Add(new Pat2Star(st));
                }
                else items.Add(new Pat2Wild());
            }
            return new Pat2Seq(items, true);
        }

        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.UNPACK_SEQUENCE)
        {
            var n = _instrs[c.I].OpArg ?? 0;
            c.I = PatNext(c.I + 1);
            PatPop(c);
            var elems = new List<PatSlot>();
            for (var i = 0; i < n; i++) elems.Add(new PatSlot());
            for (var i = n - 1; i >= 0; i--) c.Stack.Add(elems[i]);
            var items = new List<Pat2>();
            foreach (var e in elems) items.Add(ParsePat(c, e, limit));
            return new Pat2Seq(items, true);
        }

        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.UNPACK_EX)
        {
            var arg = _instrs[c.I].OpArg ?? 0;
            c.I = PatNext(c.I + 1);
            var lead = arg & 0xFF;
            var trail = arg >> 8;
            var n = lead + 1 + trail;
            PatPop(c);
            var elems = new List<PatSlot>();
            for (var i = 0; i < n; i++) elems.Add(new PatSlot());
            for (var i = n - 1; i >= 0; i--) c.Stack.Add(elems[i]);
            var items = new List<Pat2>();
            for (var i = 0; i < n; i++)
            {
                if (i == lead)
                {
                    elems[i].IsCapture = true;
                    elems[i].IsStar = true;
                    items.Add(new Pat2Star(elems[i]));
                }
                else
                {
                    items.Add(ParsePat(c, elems[i], limit));
                }
            }
            return new Pat2Seq(items, true);
        }

        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.COPY && (_instrs[c.I].OpArg ?? 0) == 1)
        {

            if (size < 0) throw new PatParseException();
            var pats = new Pat2?[size];
            var guard = 0;
            while (c.I < limit && _instrs[c.I].OpCode == OpCodes.COPY && (_instrs[c.I].OpArg ?? 0) == 1 && guard++ < 64)
            {
                c.I = PatNext(c.I + 1);
                PatCopy(c, 1);
                var idxPseudo = new PatSlot { IsPseudo = true };
                int pos;
                if (c.I < limit && _instrs[c.I].OpCode == OpCodes.LOAD_CONST)
                {
                    pos = Convert.ToInt32(_instrs[c.I].ArgVal ?? 0);
                    c.I = PatNext(c.I + 1);
                    c.Stack.Add(idxPseudo);
                }
                else if (c.I + 2 < limit && _instrs[c.I].OpCode == OpCodes.GET_LEN && _instrs[c.I + 1].OpCode == OpCodes.LOAD_CONST && _instrs[c.I + 2].OpCode == OpCodes.BINARY_OP)
                {
                    var k = Convert.ToInt32(_instrs[c.I + 1].ArgVal ?? 0);
                    pos = size - k;
                    c.I = PatNext(c.I + 3);
                    c.Stack.Add(idxPseudo);
                }
                else
                {
                    throw new PatParseException();
                }
                if (c.I >= limit || _instrs[c.I].OpCode != OpCodes.BINARY_SUBSCR) throw new PatParseException();
                c.I = PatNext(c.I + 1);
                PatPop(c);
                PatPop(c);
                if (pos < 0 || pos >= size) throw new PatParseException();
                var elem = new PatSlot();
                c.Stack.Add(elem);
                pats[pos] = ParsePat(c, elem, limit);
            }
            PatConsumeSwaps(c, limit);
            if (c.I < limit && _instrs[c.I].OpCode == OpCodes.POP_TOP)
            {
                c.I = PatNext(c.I + 1);
                PatPop(c);
            }
            var starPos = -1;
            for (var i = 0; i < size; i++)
            {
                if (pats[i] == null)
                {
                    if (starPos < 0) starPos = i;
                }
            }
            if (starPos < 0) throw new PatParseException();
            var items = new List<Pat2>();
            for (var i = 0; i < size; i++)
            {
                if (i == starPos)
                {
                    var st = new PatSlot { IsCapture = true, IsStar = true };
                    items.Add(new Pat2Star(st));
                }
                else items.Add(pats[i] ?? new Pat2Wild());
            }
            return new Pat2Seq(items, true);
        }

        throw new PatParseException();
    }

    private static void AssignPatNames(Pat2 p, List<string> names, ref int index)
    {
        switch (p)
        {
            case Pat2Cap cap when cap.Slot.Name == null && cap.Slot.Pat == null && !cap.Slot.IsStar:
                if (index < names.Count) cap.Slot.Name = names[index++];
                break;
            case Pat2Seq seq:
                foreach (var item in seq.Items) AssignPatNames(item, names, ref index);
                break;
            case Pat2Map map:
                foreach (var (_, pat) in map.Entries) AssignPatNames(pat, names, ref index);
                if (map.Rest != null) AssignPatNames(map.Rest, names, ref index);
                break;
            case Pat2Class cls:
                foreach (var arg in cls.Args) AssignPatNames(arg, names, ref index);
                break;
            case Pat2Kw kw:
                AssignPatNames(kw.Pat, names, ref index);
                break;
            case Pat2Or or:
                foreach (var alt in or.Alts) AssignPatNames(alt.Pat, names, ref index);
                break;
        }
    }

    private Pat2 ParseMapPat(PatCtx c, PatSlot slot, int limit)
    {
        c.I = PatNext(c.I);        PatNoteFail(c, _instrs[c.I]);
        c.I = PatNext(c.I + 1);
        c.Stack.Add(new PatSlot { IsPseudo = true });
        if (c.I < limit && _instrs[c.I].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
        {
            PatNoteFail(c, _instrs[c.I]);
            PatPop(c);
            c.I = PatNext(c.I + 1);
        }

        var size = -1;
        if (c.I + 3 < limit &&
            _instrs[c.I].OpCode == OpCodes.GET_LEN &&
            _instrs[c.I + 1].OpCode == OpCodes.LOAD_CONST &&
            _instrs[c.I + 2].OpCode == OpCodes.COMPARE_OP &&
            _instrs[c.I + 3].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
        {
            size = Convert.ToInt32(_instrs[c.I + 1].ArgVal ?? 0);
            PatNoteFail(c, _instrs[c.I + 3]);
            c.I = PatNext(c.I + 4);
        }

        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.POP_TOP)
        {

            PatPop(c);
            c.I = PatNext(c.I + 1);
            if (c.Stack.Count == 0 || !ReferenceEquals(c.Stack[^1], slot)) throw new PatParseException();
            PatPop(c);
            return new Pat2Map([], null);
        }

        var keys = new List<object?>();
        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.LOAD_CONST)
        {
            foreach (var k in PatTupleItems(_instrs[c.I].ArgVal)) keys.Add(k);
            c.Stack.Add(new PatSlot { IsPseudo = true });
            c.I = PatNext(c.I + 1);
        }
        else if (c.I < limit && _instrs[c.I].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
        {
            while (c.I < limit && _instrs[c.I].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
            {
                var name = _instrs[c.I].ArgRepr ?? _instrs[c.I].ArgVal?.ToString() ?? "";
                keys.Add(name);
                c.I = PatNext(c.I + 1);
                while (c.I < limit && _instrs[c.I].OpCode == OpCodes.LOAD_ATTR) c.I = PatNext(c.I + 1);
            }
            if (c.I >= limit || _instrs[c.I].OpCode != OpCodes.BUILD_TUPLE) throw new PatParseException();
            c.I = PatNext(c.I + 1);
            c.Stack.Add(new PatSlot { IsPseudo = true });
        }
        else
        {
            throw new PatParseException();
        }

        if (c.I >= limit || _instrs[c.I].OpCode != OpCodes.MATCH_KEYS) throw new PatParseException();        c.I = PatNext(c.I + 1);

        var keysSlot = PatPop(c);
        keysSlot.IsPseudo = false;
        keysSlot.IsKeys = true;
        c.Stack.Add(keysSlot);
        var valsSlot = new PatSlot();
        c.Stack.Add(valsSlot);

        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.COPY && (_instrs[c.I].OpArg ?? 0) == 1)
        {
            PatCopy(c, 1);
            c.I = PatNext(c.I + 1);
        }
        if (c.I < limit && _instrs[c.I].OpCode is OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_FALSE)
        {
            PatNoteFail(c, _instrs[c.I]);
            PatPop(c);
            c.I = PatNext(c.I + 1);
        }


        if (c.I + 1 < limit &&
            _instrs[c.I].OpCode == OpCodes.POP_TOP &&
            _instrs[c.I + 1].OpCode == OpCodes.LEGACY_COPY_DICT_WITHOUT_KEYS)
        {
            c.I = PatNext(c.I + 2);
            PatPop(c);
            var restSlot = new PatSlot { IsCapture = true, IsStar = true };
            c.Stack.Add(restSlot);
            if (c.I < limit && _instrs[c.I].OpCode == OpCodes.LEGACY_ROT_TWO)
            {
                PatRotate(c.Stack, 2);
                c.I = PatNext(c.I + 1);
            }
            if (c.I < limit && _instrs[c.I].OpCode == OpCodes.POP_TOP)
            {
                PatPop(c);
                c.I = PatNext(c.I + 1);
            }
            if (c.I < limit && IsPatStoreOp(_instrs[c.I].OpCode))
            {
                restSlot.Name = PatStoreName(_instrs[c.I]);
                PatPop(c);
                c.I = PatNext(c.I + 1);
            }
            return new Pat2Map([], new Pat2Star(restSlot));
        }


        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.LEGACY_DUP_TOP)
        {
            var mapPats = new List<Pat2>();
            while (c.I < limit && _instrs[c.I].OpCode == OpCodes.LEGACY_DUP_TOP)
            {
                if (c.I + 2 >= limit || _instrs[c.I + 1].OpCode != OpCodes.LOAD_CONST ||
                    _instrs[c.I + 2].OpCode != OpCodes.BINARY_SUBSCR)
                {
                    throw new PatParseException();
                }
                c.I = PatNext(c.I + 3);
                var elemSlot = new PatSlot();
                var tmp = new PatCtx { I = c.I, Depth = c.Depth + 1 };
                tmp.Stack.Add(elemSlot);
                Pat2 p;
                try
                {
                    p = ParsePat(tmp, elemSlot, limit);
                }
                catch (PatParseException)
                {
                    p = new Pat2Cap(elemSlot);
                }
                c.I = tmp.I;
                while (c.I < limit && IsPatRotate(_instrs[c.I].OpCode))
                {
                    c.I = PatNext(c.I + 1);
                }
                mapPats.Add(p);
            }
            if (mapPats.Count != keys.Count) throw new PatParseException();

            var mapStores = new List<string>();
            PatSlot? mapRest = null;
            var restScan = c.I;
            var popRun = 0;
            while (restScan < limit && _instrs[restScan].OpCode == OpCodes.POP_TOP)
            {
                popRun++;
                restScan++;
            }
            var hasRest = restScan < limit && _instrs[restScan].OpCode == OpCodes.LEGACY_COPY_DICT_WITHOUT_KEYS;
            if (hasRest && popRun > 1 && c.Depth > 0)
            {


                var innerEntries = new List<(ExprNode Key, Pat2 Pat)>();
                for (var i = 0; i < mapPats.Count; i++)
                {
                    innerEntries.Add((new ConstantExpr(keys[i]), mapPats[i]));
                }
                return new Pat2Map(innerEntries, null);
            }
            if (hasRest)
            {
                c.I = PatNext(restScan + 1);
                PatPop(c);
                mapRest = new PatSlot { IsCapture = true, IsStar = true };
                c.Stack.Add(mapRest);
                while (c.I < limit && IsPatRotate(_instrs[c.I].OpCode))
                {
                    c.I = PatNext(c.I + 1);
                }
                if (c.I < limit && _instrs[c.I].OpCode == OpCodes.POP_TOP)
                {
                    PatPop(c);
                    c.I = PatNext(c.I + 1);
                }
            }
            else
            {
                while (c.I < limit && _instrs[c.I].OpCode == OpCodes.POP_TOP)
                {
                    c.I = PatNext(c.I + 1);
                }
            }
            while (c.I < limit && IsPatStoreOp(_instrs[c.I].OpCode))
            {
                mapStores.Add(PatStoreName(_instrs[c.I]));
                c.I = PatNext(c.I + 1);
            }

            var nameAt = 0;
            foreach (var mapPat in mapPats)
            {
                AssignPatNames(mapPat, mapStores, ref nameAt);
            }
            if (mapRest != null && nameAt < mapStores.Count)
            {
                mapRest.Name = mapStores[nameAt++];
            }
            var mapEntries = new List<(ExprNode Key, Pat2 Pat)>();
            for (var i = 0; i < mapPats.Count; i++)
            {
                mapEntries.Add((new ConstantExpr(keys[i]), mapPats[i]));
            }
            return new Pat2Map(mapEntries, mapRest == null ? null : new Pat2Star(mapRest));
        }

        if (c.I >= limit || _instrs[c.I].OpCode != OpCodes.UNPACK_SEQUENCE) throw new PatParseException();        var n = _instrs[c.I].OpArg ?? 0;
        c.I = PatNext(c.I + 1);
        PatPop(c);
        var elems = new List<PatSlot>();
        for (var i = 0; i < n; i++) elems.Add(new PatSlot());
        for (var i = n - 1; i >= 0; i--) c.Stack.Add(elems[i]);

        var entries = new List<(ExprNode Key, Pat2 Pat)>();
        for (var i = 0; i < n; i++)
        {
            var p = ParsePat(c, elems[i], limit);
            if (i >= keys.Count) throw new PatParseException();
            var key = new ConstantExpr(keys[i]);
            entries.Add((key, p));
        }

        PatConsumeSwaps(c, limit);
        Pat2? rest = null;
        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.BUILD_MAP)
        {
            c.I = PatNext(c.I + 1);
            var dictSlot = new PatSlot();
            c.Stack.Add(dictSlot);
            if (c.I < limit && _instrs[c.I].OpCode == OpCodes.SWAP)
            {
                PatSwap(c, _instrs[c.I].OpArg ?? 0);
                c.I = PatNext(c.I + 1);
            }
            if (c.I < limit && _instrs[c.I].OpCode == OpCodes.DICT_UPDATE)
            {
                PatPop(c);
                c.I = PatNext(c.I + 1);
            }
            var copies = n;
            if (c.I < limit && _instrs[c.I].OpCode == OpCodes.UNPACK_SEQUENCE)
            {
                copies = _instrs[c.I].OpArg ?? 0;
                c.I = PatNext(c.I + 1);
                PatPop(c);
                for (var i = 0; i < copies; i++) c.Stack.Add(new PatSlot { IsPseudo = true });
            }
            for (var d = 0; d < copies; d++)
            {
                if (c.I < limit && _instrs[c.I].OpCode == OpCodes.COPY)
                {
                    PatCopy(c, _instrs[c.I].OpArg ?? 1);
                    c.I = PatNext(c.I + 1);
                }
                if (c.I < limit && _instrs[c.I].OpCode == OpCodes.SWAP)
                {
                    PatSwap(c, _instrs[c.I].OpArg ?? 0);
                    c.I = PatNext(c.I + 1);
                }
                if (c.I < limit && _instrs[c.I].OpCode == OpCodes.DELETE_SUBSCR)
                {
                    PatPop(c);
                    PatPop(c);
                    c.I = PatNext(c.I + 1);
                }
                else
                {
                    throw new PatParseException();
                }
            }
            if (c.Stack.Count == 0 || !ReferenceEquals(c.Stack[^1], dictSlot)) throw new PatParseException();
            dictSlot.IsCapture = true;
            dictSlot.IsStar = true;
            rest = new Pat2Star(dictSlot);
        }
        else
        {

            while (c.I < limit && _instrs[c.I].OpCode == OpCodes.POP_TOP && c.Stack.Count > 0 &&
                   (ReferenceEquals(c.Stack[^1], keysSlot) || ReferenceEquals(c.Stack[^1], slot)))
            {
                PatPop(c);
                c.I = PatNext(c.I + 1);
            }
        }

        return new Pat2Map(entries, rest);
    }

    private ExprNode PatReadDotted(PatCtx c, int limit)
    {
        if (c.I >= limit || _instrs[c.I].OpCode is not (OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME))
            throw new PatParseException();
        var ins = _instrs[c.I];
        ExprNode expr = new NameExpr(ins.ArgRepr ?? ins.ArgVal?.ToString() ?? throw new PatParseException("pattern class name missing"));
        c.I = PatNext(c.I + 1);
        while (c.I < limit && _instrs[c.I].OpCode == OpCodes.LOAD_ATTR)
        {
            var a = _instrs[c.I];
            expr = new AttributeExpr(expr, a.ArgRepr ?? a.ArgVal?.ToString() ?? "");
            c.I = PatNext(c.I + 1);
        }
        return expr;
    }

    private Pat2 ParseValueOrClassPat(PatCtx c, PatSlot slot, int limit)
    {
        c.I = PatNext(c.I);
        if (_instrs[c.I].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
        {
            var save = c.I;
            var expr = PatReadDotted(c, limit);
            if (c.I + 1 < limit && _instrs[c.I].OpCode == OpCodes.LOAD_CONST && _instrs[c.I + 1].OpCode == OpCodes.MATCH_CLASS)
                return ParseClassPat(c, slot, expr, limit);
            c.I = save;
        }

        ExprNode value;
        if (_instrs[c.I].OpCode == OpCodes.LOAD_CONST)
        {
            value = new ConstantExpr(_instrs[c.I].ArgVal);
            c.I = PatNext(c.I + 1);
        }
        else if (_instrs[c.I].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
        {
            value = PatReadDotted(c, limit);
        }
        else
        {
            throw new PatParseException();
        }
        c.Stack.Add(new PatSlot { IsPseudo = true });

        if (c.I >= limit || _instrs[c.I].OpCode is not (OpCodes.COMPARE_OP or OpCodes.IS_OP)) throw new PatParseException();
        c.I = PatNext(c.I + 1);
        PatPop(c);
        PatPop(c);
        if (c.I >= limit || _instrs[c.I].OpCode is not (OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)) throw new PatParseException();
        PatNoteFail(c, _instrs[c.I]);
        c.I = PatNext(c.I + 1);
        return new Pat2Val(value);
    }

    private Pat2 ParseClassPat(PatCtx c, PatSlot slot, ExprNode cls, int limit)
    {
        c.I = PatNext(c.I);
        c.Stack.Add(new PatSlot { IsPseudo = true });
        if (c.I >= limit || _instrs[c.I].OpCode != OpCodes.LOAD_CONST) throw new PatParseException();
        var attrNames = PatTupleItems(_instrs[c.I].ArgVal).Select(o => o?.ToString() ?? "").ToList();
        c.Stack.Add(new PatSlot { IsPseudo = true });
        c.I = PatNext(c.I + 1);
        if (c.I >= limit || _instrs[c.I].OpCode != OpCodes.MATCH_CLASS) throw new PatParseException();
        var nargs = _instrs[c.I].OpArg ?? 0;
        c.I = PatNext(c.I + 1);
        PatPop(c);
        PatPop(c);
        PatPop(c);
        var attrsSlot = new PatSlot();
        c.Stack.Add(attrsSlot);

        var hadCopy = c.I < limit && _instrs[c.I].OpCode == OpCodes.COPY && (_instrs[c.I].OpArg ?? 0) == 1;
        if (hadCopy)
        {
            PatCopy(c, 1);
            c.I = PatNext(c.I + 1);
        }
        if (c.I < limit && _instrs[c.I].OpCode is OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_FALSE)
        {
            PatNoteFail(c, _instrs[c.I]);


            if (hadCopy) PatPop(c);
            c.I = PatNext(c.I + 1);
        }

        var total = nargs + attrNames.Count;
        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.UNPACK_SEQUENCE)
        {
            total = _instrs[c.I].OpArg ?? total;
            c.I = PatNext(c.I + 1);
            PatPop(c);
            var elems = new List<PatSlot>();
            for (var i = 0; i < total; i++) elems.Add(new PatSlot());
            for (var i = total - 1; i >= 0; i--) c.Stack.Add(elems[i]);
            var args = new List<Pat2>();
            for (var i = 0; i < total; i++)
            {
                var p = ParsePat(c, elems[i], limit);
                if (i >= nargs && i - nargs < attrNames.Count)
                    p = new Pat2Kw(attrNames[i - nargs], p);
                args.Add(p);
            }
            return new Pat2Class(cls, args);
        }


        if (c.I < limit && _instrs[c.I].OpCode == OpCodes.LEGACY_DUP_TOP)
        {
            var pats = new List<Pat2>();
            while (c.I < limit && _instrs[c.I].OpCode == OpCodes.LEGACY_DUP_TOP)
            {
                if (c.I + 2 >= limit || _instrs[c.I + 1].OpCode != OpCodes.LOAD_CONST ||
                    _instrs[c.I + 2].OpCode != OpCodes.BINARY_SUBSCR)
                {
                    throw new PatParseException();
                }
                c.I = PatNext(c.I + 3);
                var elemSlot = new PatSlot();
                var tmp = new PatCtx { I = c.I, Depth = c.Depth + 1 };
                tmp.Stack.Add(elemSlot);
                Pat2 p;
                try
                {
                    p = ParsePat(tmp, elemSlot, limit);
                }
                catch (PatParseException)
                {
                    p = new Pat2Cap(elemSlot);
                }
                c.I = tmp.I;
                while (c.I < limit && IsPatRotate(_instrs[c.I].OpCode))
                {
                    c.I = PatNext(c.I + 1);
                }
                pats.Add(p);
            }


            while (c.I < limit && _instrs[c.I].OpCode == OpCodes.POP_TOP)
            {
                c.I = PatNext(c.I + 1);
            }
            var storeNames = new List<string>();
            while (c.I < limit && IsPatStoreOp(_instrs[c.I].OpCode))
            {
                storeNames.Add(PatStoreName(_instrs[c.I]));
                c.I = PatNext(c.I + 1);
            }
            var nameIdx = 0;
            foreach (var p in pats)
            {
                if (p is Pat2Cap cap && cap.Slot.Name == null && cap.Slot.Pat == null &&
                    !cap.Slot.IsStar && nameIdx < storeNames.Count)
                {
                    cap.Slot.Name = storeNames[nameIdx++];
                }
            }
            if (pats.Count == total)
            {
                var args = new List<Pat2>();
                for (var i = 0; i < pats.Count; i++)
                {
                    var p = pats[i];
                    if (i >= nargs && i - nargs < attrNames.Count)
                        p = new Pat2Kw(attrNames[i - nargs], p);
                    args.Add(p);
                }
                return new Pat2Class(cls, args);
            }
            throw new PatParseException();
        }

        if (total != 0) throw new PatParseException();
        return TryWrapClassAsCapture(c, new Pat2Class(cls, []), limit);
    }

    private Pat2 TryWrapClassAsCapture(PatCtx c, Pat2 pattern, int limit)
    {
        if (c.I >= limit || _instrs[c.I].OpCode != OpCodes.POP_TOP) return pattern;
        var save = c.I;
        c.I = PatNext(c.I + 1);
        if (c.I < limit && IsPatStoreOp(_instrs[c.I].OpCode))
        {
            var slot = new PatSlot { Name = PatStoreName(_instrs[c.I]), Pat = pattern };
            c.I = PatNext(c.I + 1);
            return new Pat2Cap(slot);
        }
        c.I = save;
        return pattern;
    }

    private bool TryRunPatCleanup(PatCtx c, int limit)
    {
        c.I = PatNext(c.I);
        var guard = 0;
        while (c.I < limit && c.Stack.Count > 0 && guard++ < 4096)
        {
            var ins = _instrs[c.I];            if (IsPatRotate(ins.OpCode))
            {
                PatRotate(c.Stack, PatRotateCount(ins));
                c.I = PatNext(c.I + 1);
                continue;
            }
            if (ins.OpCode == OpCodes.SWAP)
            {
                PatSwap(c, ins.OpArg ?? 0);
                c.I = PatNext(c.I + 1);
                continue;
            }
            if (ins.OpCode == OpCodes.COPY)
            {
                PatCopy(c, ins.OpArg ?? 1);
                c.I = PatNext(c.I + 1);
                continue;
            }
            if (ins.OpCode == OpCodes.POP_TOP)
            {
                var s = PatPop(c);
                c.I = PatNext(c.I + 1);
                if (s.IsCapture && s.Name == null && !s.IsStar) c.PendingPopped.Add(s);
                continue;
            }
            if (IsPatStoreOp(ins.OpCode))
            {
                var s = PatPop(c);
                c.I = PatNext(c.I + 1);
                var name = PatStoreName(ins);
                if (s.IsCapture && s.Name == null)
                {
                    s.Name = name;
                }
                else if (c.PendingPopped.Count > 0)
                {


                    var idx = c.PendingPopped.FindLastIndex(p => p.Pat != null);
                    if (idx < 0) idx = c.PendingPopped.Count - 1;
                    var pend = c.PendingPopped[idx];
                    c.PendingPopped.RemoveAt(idx);
                    pend.Name = name;
                }
                else
                {
                    return false;
                }
                continue;
            }
            break;
        }


        foreach (var s in c.Stack)
        {
            if (s.IsCapture || s.IsStar) return false;
        }
        return true;
    }

    private bool TryDecompileGuardExpr(int startIdx, int endIdx, int failOff, out ExprNode? guard, out int bodyStart)
    {
        guard = null;
        bodyStart = startIdx;
        var lastJump = -1;
        for (var i = startIdx; i < endIdx && i < _instrs.Count; i++)
        {
            if (_instrs[i].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE &&
                (_instrs[i].TargetOffset ?? -1) >= failOff)
            {
                lastJump = i;
            }
        }
        if (lastJump < 0) return false;
        var gStack = new SymbolicStack();
        try
        {
            DecompileRange(startIdx, lastJump, gStack);
        }
        catch (Exception)
        {
            return false;
        }
        if (gStack.Count == 0) return false;
        guard = gStack.Pop();
        bodyStart = lastJump + 1;
        while (bodyStart < endIdx && _instrs[bodyStart].OpCode == OpCodes.POP_TOP) bodyStart++;
        return true;
    }

    private bool PatJumpsToFail(int startIdx, int endIdx, int failOff)
    {
        for (var i = startIdx; i < endIdx && i < _instrs.Count; i++)
        {
            if (_instrs[i].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
            {
                if ((_instrs[i].TargetOffset ?? -1) >= failOff) return true;
            }
        }
        return false;
    }

    private bool LooksLikeMatchHeader(int idx, int endIdx)
    {
        if (idx + 1 >= endIdx) return false;
        var nextOp = _instrs[idx + 1].OpCode;
        return nextOp is OpCodes.MATCH_SEQUENCE or OpCodes.MATCH_MAPPING or OpCodes.MATCH_KEYS or OpCodes.COPY
            || (nextOp is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME && idx + 3 < endIdx && _instrs[idx + 3].OpCode == OpCodes.MATCH_CLASS)
            || (nextOp is OpCodes.LOAD_CONST or OpCodes.LOAD_NAME or OpCodes.LOAD_GLOBAL && idx + 2 < endIdx && _instrs[idx + 2].OpCode is OpCodes.IS_OP or OpCodes.COMPARE_OP);
    }

    private bool TryParseCaseV2(int startIdx, int endIdx, out PatternNode pattern, out int afterPat, out List<int> boundaries, out int nextCaseIdx)
    {
        pattern = null!;
        afterPat = startIdx;
        boundaries = [];
        nextCaseIdx = startIdx;
        if ((_instrs[startIdx].OpCode == OpCodes.COPY && (_instrs[startIdx].OpArg ?? 0) == 1) ||
            _instrs[startIdx].OpCode == OpCodes.LEGACY_DUP_TOP)
        {
            if (TryParseCaseAttemptV2(startIdx, endIdx, true, out pattern, out afterPat, out boundaries, out nextCaseIdx))
                return true;
        }
        return TryParseCaseAttemptV2(startIdx, endIdx, false, out pattern, out afterPat, out boundaries, out nextCaseIdx);
    }

    private bool TryParseCaseAttemptV2(int startIdx, int endIdx, bool hasCaseCopy, out PatternNode pattern, out int afterPat, out List<int> boundaries, out int nextCaseIdx)
    {
        pattern = null!;
        afterPat = startIdx;
        boundaries = [];
        nextCaseIdx = startIdx;
        var c = new PatCtx { I = startIdx, CaseStart = startIdx };
        var subject = new PatSlot();
        c.Stack.Add(subject);
        if (hasCaseCopy)
        {
            c.Stack.Add(PatCloneSlot(subject));
            c.I = PatNext(c.I + 1);
        }
        Pat2 pat;
        try
        {
            pat = ParsePat(c, c.Stack[^1], endIdx);
            if (!TryRunPatCleanup(c, endIdx))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is PatParseException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
        {
            return false;
        }


        var named = new HashSet<PatSlot>();
        foreach (var s in c.PendingPopped)
        {
            if (s.Name == null && s.Pat != null) return false;
        }
        PropagateOrNames(pat);

        var bounds = new List<int>();
        foreach (var off in c.FailTargets)
        {
            if (_offsetToIndex.TryGetValue(off, out var bi)) bounds.Add(bi);
        }
        bounds.Sort();
        boundaries = bounds;

        var maxOff = c.FailTargets.Count > 0 ? c.FailTargets.Max() : -1;
        if (maxOff < 0 || !_offsetToIndex.TryGetValue(maxOff, out var nextIdx))
        {
            nextCaseIdx = endIdx;
        }
        else
        {
            var p = nextIdx;
            var guard = 0;
            while (p < endIdx && guard++ < 64)
            {
                if (_instrs[p].OpCode == OpCodes.POP_TOP) { p++; continue; }
                if (_instrs[p].OpCode == OpCodes.JUMP_FORWARD && _instrs[p].TargetOffset is int t && _offsetToIndex.TryGetValue(t, out var j))
                {
                    p = j;
                    while (p < endIdx && _instrs[p].OpCode == OpCodes.POP_TOP) p++;
                }
                break;
            }
            nextCaseIdx = p;
        }

        pattern = ResolvePat2(pat);
        afterPat = c.I;
        return true;
    }

    private bool TryDecompileMatchStmtV2(int idx, int endIdx, SymbolicStack stack, out StmtNode matchStmt, out int nextIdx)
    {
        matchStmt = null!;
        nextIdx = idx;
        if (stack.Count == 0) return false;
        if (idx >= endIdx) return false;
        var ins = _instrs[idx];
        var startsWithCopy = (ins.OpCode == OpCodes.COPY && (ins.OpArg ?? 0) == 1) ||
                             ins.OpCode == OpCodes.LEGACY_DUP_TOP;
        if (startsWithCopy)
        {
            if (!LooksLikeMatchHeader(idx, endIdx)) return false;
        }
        else
        {


            var op = ins.OpCode;
            var isClassHeader = false;
            if (op is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
            {
                var p = idx + 1;
                while (p < endIdx && _instrs[p].OpCode == OpCodes.LOAD_ATTR) p++;
                isClassHeader = p + 1 < endIdx && _instrs[p].OpCode == OpCodes.LOAD_CONST && _instrs[p + 1].OpCode == OpCodes.MATCH_CLASS;
            }
            if (op is not (OpCodes.MATCH_SEQUENCE or OpCodes.MATCH_MAPPING) && !isClassHeader)
                return false;
        }

        var subject = stack.Peek();
        var cases = new List<MatchCase>();
        var cur = idx;
        var matchExitOffset = -1;
        var first = true;
        var loops = 0;

        while (cur < endIdx && loops++ < 256)
        {
            if (_instrs[cur].OpCode == OpCodes.NOP)
            {
                var limit = endIdx;
                if (matchExitOffset >= 0 && _offsetToIndex.TryGetValue(matchExitOffset, out var meIdx))
                    limit = Math.Min(endIdx, meIdx);
                var defBody = DecompileRange(cur + 1, limit, stack.Clone());
                cases.Add(new MatchCase(new MatchWildcardPattern(), null, defBody));
                cur = limit;
                break;
            }

            if (!TryParseCaseV2(cur, endIdx, out var pat, out var afterPat, out var boundaries, out var nextCaseIdx))
            {
                if (first) return false;
                break;
            }
            first = false;

            var bodyStart = afterPat;
            var bodyEnd = nextCaseIdx;
            foreach (var b in boundaries)
            {
                if (b >= afterPat && b < bodyEnd) { bodyEnd = b; break; }
            }

            ExprNode? guard = null;
            var failOff = boundaries.Count > 0
                ? _instrs[boundaries[^1]].Offset
                : (nextCaseIdx < _instrs.Count ? _instrs[nextCaseIdx].Offset : int.MaxValue);
            if (bodyStart < bodyEnd && TryExtractMatchGuard(bodyStart, bodyEnd, out var g, out var gBodyIdx) && g != null &&
                PatJumpsToFail(bodyStart, gBodyIdx, failOff))
            {
                guard = g;
                bodyStart = gBodyIdx;


                if (bodyStart < bodyEnd && _instrs[bodyStart].OpCode == OpCodes.POP_TOP) bodyStart++;
            }
            else if (bodyStart < bodyEnd && TryDecompileGuardExpr(bodyStart, bodyEnd, failOff, out var g2, out var g2Body))
            {


                guard = g2;
                bodyStart = g2Body;
                if (bodyStart < bodyEnd && _instrs[bodyStart].OpCode == OpCodes.POP_TOP) bodyStart++;
            }

            while (bodyEnd > bodyStart && _instrs[bodyEnd - 1].OpCode is OpCodes.NOP or OpCodes.POP_TOP) bodyEnd--;
            if (bodyEnd > bodyStart && _instrs[bodyEnd - 1].OpCode == OpCodes.JUMP_FORWARD)
            {
                var jt = _instrs[bodyEnd - 1].TargetOffset ?? -1;
                if (jt >= 0 && !_activeLoopExits.Contains(jt))
                {
                    if (matchExitOffset < 0 || jt < matchExitOffset) matchExitOffset = jt;
                    bodyEnd--;
                }
            }

            var body = DecompileRange(bodyStart, bodyEnd, stack.Clone());


            if (body.Count > 0 && subject is NameExpr subjectName &&
                body[0] is ExprStmt { Expression: NameExpr bodyName } &&
                bodyName.Name == subjectName.Name)
            {
                body.RemoveAt(0);
            }
            cases.Add(new MatchCase(pat, guard, body));

            if (nextCaseIdx <= cur) break;
            cur = nextCaseIdx;
        }

        if (cases.Count == 0) return false;
        stack.Pop();
        matchStmt = new MatchStmt(subject, cases);
        nextIdx = matchExitOffset >= 0 && _offsetToIndex.TryGetValue(matchExitOffset, out var exitIdx) ? exitIdx : cur;
        if (nextIdx < idx) nextIdx = endIdx;
        return true;
    }


    private bool TryDecompileListGenexpr315(int idx, int endIdx, SymbolicStack stack, out int nextIdx)
    {
        nextIdx = idx;
        if (idx + 4 >= endIdx) return false;
        if (_instrs[idx].OpCode is not (OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)) return false;
        var listName = _instrs[idx].ArgRepr ?? _instrs[idx].ArgVal?.ToString() ?? "";
        if (listName != "list") return false;
        if (_instrs[idx + 1].OpCode != OpCodes.COPY || (_instrs[idx + 1].OpArg ?? 0) != 1) return false;
        if (_instrs[idx + 2].OpCode != OpCodes.LOAD_CONST) return false;
        var constName = _instrs[idx + 2].ArgRepr ?? _instrs[idx + 2].ArgVal?.ToString() ?? "";
        if (constName != "list") return false;
        if (_instrs[idx + 3].OpCode != OpCodes.IS_OP || (_instrs[idx + 3].OpArg ?? 0) != 0) return false;
        if (_instrs[idx + 4].OpCode != OpCodes.POP_JUMP_IF_FALSE) return false;

        var cur = idx + 5;
        while (cur < endIdx && _instrs[cur].OpCode is OpCodes.NOT_TAKEN or OpCodes.NOP) cur++;
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.POP_TOP) return false;
        cur++;
        while (cur < endIdx && _instrs[cur].OpCode is OpCodes.NOT_TAKEN or OpCodes.NOP) cur++;
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.BUILD_LIST || (_instrs[cur].OpArg ?? 0) != 0)
            return false;
        var genStart = cur + 1;

        var forIterIdx = genStart;
        while (forIterIdx < endIdx && _instrs[forIterIdx].OpCode != OpCodes.FOR_ITER) forIterIdx++;
        if (forIterIdx >= endIdx) return false;
        var pushNullIdx = forIterIdx;
        while (pushNullIdx > genStart && _instrs[pushNullIdx - 1].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN)
            pushNullIdx--;
        if (pushNullIdx > genStart && _instrs[pushNullIdx - 1].OpCode == OpCodes.PUSH_NULL) pushNullIdx--;

        var genStack = new SymbolicStack();
        try
        {
            DecompileRange(genStart, pushNullIdx, genStack);
        }
        catch (Exception)
        {
            return false;
        }
        if (genStack.Count != 1 || genStack.Peek() is not GeneratorExpr genExpr) return false;

        if (_instrs[forIterIdx].TargetOffset is not int loopExitTarget ||
            !_offsetToIndex.TryGetValue(loopExitTarget, out var afterLoop))
        {
            return false;
        }


        while (afterLoop < endIdx &&
               (_instrs[afterLoop].OpName is "END_FOR" or "POP_ITER" ||
                _instrs[afterLoop].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN))
        {
            afterLoop++;
        }


        if (_instrs[idx + 4].TargetOffset is not int fallbackOffset ||
            !_offsetToIndex.TryGetValue(fallbackOffset, out var fallbackIdx))
        {
            return false;
        }
        if (afterLoop < endIdx && _instrs[afterLoop].OpCode == OpCodes.JUMP_FORWARD &&
            _instrs[afterLoop].TargetOffset is int joinOffset &&
            _offsetToIndex.TryGetValue(joinOffset, out var joinIdx))
        {
            var fallbackStart = afterLoop + 1;
            while (fallbackStart < endIdx &&
                   _instrs[fallbackStart].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN)
            {
                fallbackStart++;
            }
            if (fallbackIdx != fallbackStart) return false;
            nextIdx = joinIdx;
        }
        else
        {
            if (fallbackIdx < afterLoop) return false;
            nextIdx = afterLoop;
        }

        stack.Push(new CallExpr(new NameExpr("list"), [genExpr], []));
        return true;
    }

    private bool TryDecompileAnyOrCaseLegacy(int idx, int endIdx, SymbolicStack stack, out MatchStmt matchStmt, out int nextIdx)
    {
        matchStmt = null!;
        nextIdx = idx;
        if (TryDecompileOrPatternLegacy(idx, endIdx, stack, out var valueStmt, out var valueNext) &&
            valueStmt is MatchStmt vm)
        {
            matchStmt = vm; nextIdx = valueNext; return true;
        }
        var classStack = stack.Clone();
        if (TryDecompileClassOrPatternLegacy(idx, endIdx, classStack, out var classStmt, out var classNext) &&
            classStmt is MatchStmt cm)
        {
            matchStmt = cm; nextIdx = classNext; return true;
        }
        var seqStack = stack.Clone();
        if (TryDecompileSeqOrPatternLegacy(idx, endIdx, seqStack, out var seqStmt, out var seqNext) &&
            seqStmt is MatchStmt sm)
        {
            matchStmt = sm; nextIdx = seqNext; return true;
        }
        return false;
    }


    private bool TryDecompileSeqOrPatternLegacy(int idx, int endIdx, SymbolicStack stack, out StmtNode matchStmt, out int nextIdx)
    {
        matchStmt = null!;
        nextIdx = idx;
        if (idx + 6 >= endIdx || stack.Count == 0) return false;
        if (_instrs[idx].OpCode != OpCodes.LEGACY_DUP_TOP ||
            _instrs[idx + 1].OpCode != OpCodes.MATCH_SEQUENCE) return false;

        var subject = stack.Peek();
        var alternatives = new List<int>();
        var cur = idx;
        var failIdx = -1;
        var mergeIdx = -1;
        var guard = 0;
        while (guard++ < 64)
        {
            if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.LEGACY_DUP_TOP) return false;
            if (cur + 1 >= endIdx || _instrs[cur + 1].OpCode != OpCodes.MATCH_SEQUENCE) return false;
            if (cur + 5 >= endIdx || _instrs[cur + 2].OpCode != OpCodes.POP_JUMP_IF_FALSE ||
                _instrs[cur + 3].OpCode != OpCodes.GET_LEN ||
                _instrs[cur + 4].OpCode != OpCodes.LOAD_CONST ||
                _instrs[cur + 5].OpCode != OpCodes.COMPARE_OP) return false;
            var n = Convert.ToInt32(_instrs[cur + 4].ArgVal ?? 0);
            if (n != 1) return false;
            var jumpIdx = cur + 6;
            while (jumpIdx < endIdx && _instrs[jumpIdx].OpCode is OpCodes.EXTENDED_ARG or OpCodes.NOP or OpCodes.NOT_TAKEN) jumpIdx++;
            if (jumpIdx >= endIdx || _instrs[jumpIdx].OpCode != OpCodes.POP_JUMP_IF_FALSE) return false;
            var failTarget = _instrs[jumpIdx].TargetOffset ?? -1;
            if (jumpIdx + 1 >= endIdx || _instrs[jumpIdx + 1].OpCode != OpCodes.UNPACK_SEQUENCE) return false;
            if (jumpIdx + 2 >= endIdx || _instrs[jumpIdx + 2].OpCode != OpCodes.JUMP_FORWARD) return false;
            if (_instrs[jumpIdx + 2].TargetOffset is not int mergeTarget ||
                !_offsetToIndex.TryGetValue(mergeTarget, out mergeIdx)) return false;
            if (!_offsetToIndex.TryGetValue(failTarget, out var nextAlt)) return false;
            alternatives.Add(n);

            if (nextAlt < endIdx && _instrs[nextAlt].OpCode == OpCodes.POP_TOP &&
                nextAlt + 2 < endIdx && _instrs[nextAlt + 1].OpCode == OpCodes.LEGACY_DUP_TOP &&
                _instrs[nextAlt + 2].OpCode == OpCodes.MATCH_SEQUENCE)
            {
                cur = nextAlt + 1;
                continue;
            }
            failIdx = nextAlt;
            break;
        }
        if (failIdx < 0 || mergeIdx < 0) return false;

        var matchEndIdx = failIdx;
        if (failIdx < endIdx && _instrs[failIdx].OpCode == OpCodes.POP_TOP)
        {
            var afterFail = failIdx + 1;
            if (afterFail < endIdx && _instrs[afterFail].OpCode == OpCodes.POP_TOP) afterFail++;
            if (afterFail < endIdx && _instrs[afterFail].OpCode == OpCodes.JUMP_FORWARD &&
                _instrs[afterFail].TargetOffset is int endTarget &&
                _offsetToIndex.TryGetValue(endTarget, out var endIdx2))
            {
                matchEndIdx = endIdx2;
            }
        }

        var bodyCur = mergeIdx;
        if (bodyCur < endIdx && _instrs[bodyCur].OpCode == OpCodes.LEGACY_ROT_TWO) bodyCur++;
        if (bodyCur < endIdx && _instrs[bodyCur].OpCode == OpCodes.POP_TOP) bodyCur++;
        string? capture = null;
        if (bodyCur < endIdx && IsPatStoreOp(_instrs[bodyCur].OpCode))
        {
            capture = PatStoreName(_instrs[bodyCur]);
            bodyCur++;
        }
        if (capture == null) return false;

        var bodyEnd = bodyCur;
        while (bodyEnd < matchEndIdx &&
               _instrs[bodyEnd].OpCode is not (OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST))
        {
            bodyEnd++;
        }
        if (bodyEnd < matchEndIdx) bodyEnd++;
        var caseBody = DecompileRange(bodyCur, bodyEnd, stack.Clone());

        var seqPatterns = alternatives.Select(_ => (PatternNode)new MatchSequencePattern(
            [new MatchAsPattern(null, capture)], true)).ToList();
        PatternNode pat = seqPatterns.Count == 1 ? seqPatterns[0] : new MatchOrPattern(seqPatterns);
        var cases = new List<MatchCase> { new(pat, null, caseBody) };

        var afterMatch = matchEndIdx;
        if (matchEndIdx < endIdx && _instrs[matchEndIdx].OpCode == OpCodes.POP_TOP) afterMatch = matchEndIdx + 1;
        if (afterMatch < endIdx && _instrs[afterMatch].OpCode == OpCodes.NOP)
        {
            var defStart = afterMatch + 1;
            var defEnd = defStart;
            while (defEnd < endIdx && _instrs[defEnd].OpCode is not (OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RAISE_VARARGS)) defEnd++;
            if (defEnd < endIdx) defEnd++;
            cases.Add(new MatchCase(new MatchWildcardPattern(), null, DecompileRange(defStart, defEnd, stack.Clone())));
            afterMatch = defEnd;
        }

        stack.Pop();
        matchStmt = new MatchStmt(subject, cases);
        nextIdx = afterMatch;
        return true;
    }


    private bool TryDecompileClassOrPatternLegacy(int idx, int endIdx, SymbolicStack stack, out StmtNode matchStmt, out int nextIdx)
    {
        matchStmt = null!;
        nextIdx = idx;
        if (idx + 6 >= endIdx || stack.Count == 0) return false;
        if (_instrs[idx].OpCode != OpCodes.LEGACY_DUP_TOP ||
            _instrs[idx + 1].OpCode != OpCodes.LEGACY_DUP_TOP) return false;
        if (_instrs[idx + 2].OpCode is not (OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)) return false;
        if (_instrs[idx + 3].OpCode != OpCodes.LOAD_CONST ||
            _instrs[idx + 4].OpCode != OpCodes.MATCH_CLASS ||
            _instrs[idx + 5].OpCode != OpCodes.POP_JUMP_IF_FALSE) return false;

        var subject = stack.Peek();
        var alternatives = new List<PatternNode>();
        var cur = idx;
        while (cur < endIdx && _instrs[cur].OpCode == OpCodes.LEGACY_DUP_TOP) cur++;
        var failIdx = -1;
        var mergeIdx = -1;
        var guard = 0;
        while (guard++ < 64)
        {
            if (cur < endIdx && _instrs[cur].OpCode == OpCodes.POP_TOP) cur++;
            if (cur < endIdx && _instrs[cur].OpCode == OpCodes.LEGACY_DUP_TOP) cur++;
            if (cur >= endIdx || _instrs[cur].OpCode is not (OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)) return false;
            ExprNode clsExpr = new NameExpr(_instrs[cur].ArgRepr ?? _instrs[cur].ArgVal?.ToString() ?? throw new PatParseException("pattern class name missing"));
            cur++;
            while (cur < endIdx && _instrs[cur].OpCode == OpCodes.LOAD_ATTR)
            {
                clsExpr = new AttributeExpr(clsExpr, _instrs[cur].ArgRepr ?? _instrs[cur].ArgVal?.ToString() ?? "");
                cur++;
            }
            if (cur + 4 >= endIdx || _instrs[cur].OpCode != OpCodes.LOAD_CONST ||
                _instrs[cur + 1].OpCode != OpCodes.MATCH_CLASS ||
                _instrs[cur + 2].OpCode != OpCodes.POP_JUMP_IF_FALSE ||
                _instrs[cur + 3].OpCode != OpCodes.POP_TOP ||
                _instrs[cur + 4].OpCode != OpCodes.JUMP_FORWARD) return false;
            var matchArgCount = _instrs[cur + 1].OpArg ?? 0;
            if (matchArgCount != 0) return false;
            alternatives.Add(new MatchClassPattern(clsExpr, []));
            if (_instrs[cur + 4].TargetOffset is not int mergeTarget ||
                !_offsetToIndex.TryGetValue(mergeTarget, out mergeIdx)) return false;
            if (_instrs[cur + 2].TargetOffset is not int falseTarget ||
                !_offsetToIndex.TryGetValue(falseTarget, out var nextAltIdx)) return false;
            if (nextAltIdx >= endIdx || _instrs[nextAltIdx].OpCode != OpCodes.POP_TOP ||
                nextAltIdx + 1 >= endIdx || _instrs[nextAltIdx + 1].OpCode != OpCodes.LEGACY_DUP_TOP)
            {
                failIdx = nextAltIdx;
                break;
            }
            cur = nextAltIdx + 1;
        }
        if (failIdx < 0 || mergeIdx < 0) return false;

        var matchEndIdx = failIdx;
        if (failIdx < endIdx && _instrs[failIdx].OpCode == OpCodes.POP_TOP)
        {
            var afterFail = failIdx + 1;
            if (afterFail < endIdx && _instrs[afterFail].OpCode == OpCodes.POP_TOP) afterFail++;
            if (afterFail < endIdx && _instrs[afterFail].OpCode == OpCodes.JUMP_FORWARD &&
                _instrs[afterFail].TargetOffset is int endTarget &&
                _offsetToIndex.TryGetValue(endTarget, out var endIdx2))
            {
                matchEndIdx = endIdx2;
            }
        }

        var bodyCur = mergeIdx;
        if (bodyCur < endIdx && _instrs[bodyCur].OpCode == OpCodes.POP_TOP) bodyCur++;
        string? capture = null;
        if (bodyCur < endIdx && IsPatStoreOp(_instrs[bodyCur].OpCode))
        {
            capture = PatStoreName(_instrs[bodyCur]);
            bodyCur++;
        }
        ExprNode? caseGuard = null;
        if (bodyCur < endIdx && TryExtractMatchGuard(bodyCur, matchEndIdx, out var g, out var gBodyIdx) && g != null)
        {
            caseGuard = g;
            bodyCur = gBodyIdx;
            if (bodyCur < endIdx && _instrs[bodyCur].OpCode == OpCodes.POP_TOP) bodyCur++;
        }
        var bodyEnd = bodyCur;
        while (bodyEnd < matchEndIdx &&
               _instrs[bodyEnd].OpCode is not (OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST))
        {
            bodyEnd++;
        }
        if (bodyEnd < matchEndIdx) bodyEnd++;
        var caseBody = DecompileRange(bodyCur, bodyEnd, stack.Clone());

        PatternNode pat = alternatives.Count == 1 ? alternatives[0] : new MatchOrPattern(alternatives);
        if (capture != null) pat = new MatchAsPattern(pat, capture);
        var cases = new List<MatchCase> { new(pat, caseGuard, caseBody) };

        var afterMatch = matchEndIdx;
        if (matchEndIdx < endIdx && _instrs[matchEndIdx].OpCode == OpCodes.POP_TOP) afterMatch = matchEndIdx + 1;
        if (afterMatch < endIdx && _instrs[afterMatch].OpCode == OpCodes.NOP)
        {
            var defStart = afterMatch + 1;
            var defEnd = defStart;
            while (defEnd < endIdx && _instrs[defEnd].OpCode is not (OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RAISE_VARARGS)) defEnd++;
            if (defEnd < endIdx) defEnd++;
            cases.Add(new MatchCase(new MatchWildcardPattern(), null, DecompileRange(defStart, defEnd, stack.Clone())));
            afterMatch = defEnd;
        }

        stack.Pop();
        matchStmt = new MatchStmt(subject, cases);
        nextIdx = afterMatch;
        return true;
    }


    private bool TryDecompileOrPatternLegacy(int idx, int endIdx, SymbolicStack stack, out StmtNode matchStmt, out int nextIdx)
    {
        matchStmt = null!;
        nextIdx = idx;
        if (idx + 3 >= endIdx || stack.Count == 0) return false;
        var subject = stack.Peek();
        if (_instrs[idx].OpCode != OpCodes.LEGACY_DUP_TOP ||
            _instrs[idx + 1].OpCode != OpCodes.LEGACY_DUP_TOP) return false;
        if (_instrs[idx + 2].OpCode != OpCodes.LOAD_CONST) return false;
        if (_instrs[idx + 3].OpCode is not (OpCodes.COMPARE_OP or OpCodes.IS_OP)) return false;
        if (idx + 4 >= endIdx || _instrs[idx + 4].OpCode != OpCodes.POP_JUMP_IF_FALSE) return false;
        if (idx + 5 >= endIdx || _instrs[idx + 5].OpCode != OpCodes.JUMP_FORWARD) return false;

        var alternatives = new List<PatternNode>();
        var cur = idx;
        while (cur < endIdx && _instrs[cur].OpCode == OpCodes.LEGACY_DUP_TOP) cur++;
        var failIdx = -1;
        var mergeIdx = -1;
        var guardLimit = 0;
        while (guardLimit++ < 64)
        {
            if (cur + 4 >= endIdx || _instrs[cur].OpCode != OpCodes.LOAD_CONST ||
                _instrs[cur + 1].OpCode is not (OpCodes.COMPARE_OP or OpCodes.IS_OP) ||
                _instrs[cur + 2].OpCode != OpCodes.POP_JUMP_IF_FALSE ||
                _instrs[cur + 3].OpCode != OpCodes.JUMP_FORWARD)
            {
                return false;
            }
            alternatives.Add(new MatchValuePattern(new ConstantExpr(_instrs[cur].ArgVal)));
            if (_instrs[cur + 3].TargetOffset is not int mergeTarget ||
                !_offsetToIndex.TryGetValue(mergeTarget, out mergeIdx)) return false;
            if (_instrs[cur + 2].TargetOffset is not int falseTarget ||
                !_offsetToIndex.TryGetValue(falseTarget, out var nextAltIdx)) return false;


            if (nextAltIdx >= endIdx || _instrs[nextAltIdx].OpCode != OpCodes.LEGACY_DUP_TOP)
            {
                failIdx = nextAltIdx;
                break;
            }
            cur = nextAltIdx + 1;
        }
        if (failIdx < 0 || mergeIdx < 0) return false;

        var matchEndIdx = failIdx;
        if (failIdx < endIdx && _instrs[failIdx].OpCode == OpCodes.POP_TOP)
        {
            var afterFail = failIdx + 1;
            if (afterFail < endIdx && _instrs[afterFail].OpCode == OpCodes.JUMP_FORWARD &&
                _instrs[afterFail].TargetOffset is int endTarget &&
                _offsetToIndex.TryGetValue(endTarget, out var endIdx2))
            {
                matchEndIdx = endIdx2;
            }
        }

        var bodyCur = mergeIdx;
        if (bodyCur < endIdx && _instrs[bodyCur].OpCode == OpCodes.POP_TOP) bodyCur++;
        string? capture = null;
        if (bodyCur < endIdx && IsPatStoreOp(_instrs[bodyCur].OpCode))
        {
            capture = PatStoreName(_instrs[bodyCur]);
            bodyCur++;
        }

        ExprNode? guard = null;
        var failOffset = failIdx < endIdx ? _instrs[failIdx].Offset : int.MaxValue;
        if (bodyCur < endIdx &&
            TryExtractMatchGuard(bodyCur, matchEndIdx, out var g, out var gBodyIdx) && g != null)
        {
            guard = g;
            bodyCur = gBodyIdx;
            if (bodyCur < endIdx && _instrs[bodyCur].OpCode == OpCodes.POP_TOP) bodyCur++;
        }

        var bodyEnd = bodyCur;
        while (bodyEnd < matchEndIdx &&
               _instrs[bodyEnd].OpCode is not (OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST))
        {
            bodyEnd++;
        }
        if (bodyEnd < matchEndIdx) bodyEnd++;
        else bodyEnd = matchEndIdx;
        var caseBody = DecompileRange(bodyCur, bodyEnd, stack.Clone());
        PatternNode pat = alternatives.Count == 1 ? alternatives[0] : new MatchOrPattern(alternatives);
        if (capture != null) pat = new MatchAsPattern(pat, capture);

        var cases = new List<MatchCase> { new(pat, guard, caseBody) };

        var afterMatch = matchEndIdx;
        if (matchEndIdx < endIdx && _instrs[matchEndIdx].OpCode == OpCodes.POP_TOP) afterMatch = matchEndIdx + 1;
        if (afterMatch < endIdx && _instrs[afterMatch].OpCode == OpCodes.NOP)
        {
            var defStart = afterMatch + 1;
            var defEnd = defStart;
            while (defEnd < endIdx && _instrs[defEnd].OpCode is not (OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RAISE_VARARGS)) defEnd++;
            if (defEnd < endIdx) defEnd++;
            cases.Add(new MatchCase(new MatchWildcardPattern(), null, DecompileRange(defStart, defEnd, stack.Clone())));
            afterMatch = defEnd;
        }


        if (afterMatch < endIdx && _instrs[afterMatch].OpCode == OpCodes.LEGACY_DUP_TOP)
        {
            var nextStack = stack.Clone();
            if (TryDecompileAnyOrCaseLegacy(afterMatch, endIdx, nextStack, out var nextMatch, out var nextAfter))
            {
                cases.AddRange(nextMatch.Cases);
                afterMatch = nextAfter;
            }
        }

        stack.Pop();
        matchStmt = new MatchStmt(subject, cases);
        nextIdx = afterMatch;
        return true;
    }

    private bool LooksLikeMatchStart(int idx, int endIdx)
    {
        if (idx >= endIdx) return false;
        var ins = _instrs[idx];
        if ((ins.OpCode == OpCodes.COPY && (ins.OpArg ?? 0) == 1) || ins.OpCode == OpCodes.LEGACY_DUP_TOP)
            return LooksLikeMatchHeader(idx, endIdx);
        if (ins.OpCode is OpCodes.MATCH_SEQUENCE or OpCodes.MATCH_MAPPING) return true;
        if (ins.OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
        {
            var p = idx + 1;
            while (p < endIdx && _instrs[p].OpCode == OpCodes.LOAD_ATTR) p++;
            return p + 1 < endIdx && _instrs[p].OpCode == OpCodes.LOAD_CONST && _instrs[p + 1].OpCode == OpCodes.MATCH_CLASS;
        }
        return false;
    }

    private bool TryDecompileMatchStmt(int idx, int endIdx, SymbolicStack stack, out StmtNode matchStmt, out int nextIdx)
    {
        matchStmt = null!;
        nextIdx = idx;
        if (!LooksLikeMatchStart(idx, endIdx)) return false;


        if (MatchShapeIsAdvanced(idx, endIdx))
        {
            var advancedStack = stack.Clone();
            if (TryDecompileMatchStmtV2(idx, endIdx, stack, out matchStmt, out nextIdx)) return true;
            stack.RestoreFrom(advancedStack);
            return TryDecompileMatchStmtLegacy(idx, endIdx, stack, out matchStmt, out nextIdx);
        }

        var saved = stack.Clone();
        try
        {
            if (TryDecompileMatchStmtLegacy(idx, endIdx, stack, out matchStmt, out nextIdx)) return true;
        }
        catch (Exception ex) when (ex is not FailFastException)
        {

        }
        stack.RestoreFrom(saved);


        if (!HasRealMatchOpcode(idx, endIdx))
        {
            matchStmt = null!;
            nextIdx = idx;
            return false;
        }
        try
        {
            return TryDecompileMatchStmtV2(idx, endIdx, stack, out matchStmt, out nextIdx);
        }
        catch (Exception ex) when (ex is not FailFastException)
        {
            matchStmt = null!;
            nextIdx = idx;
            return false;
        }
    }

    private bool HasRealMatchOpcode(int idx, int endIdx)
    {
        for (var i = idx; i < endIdx; i++)
        {
            if (_instrs[i].OpCode is OpCodes.MATCH_SEQUENCE or OpCodes.MATCH_MAPPING or OpCodes.MATCH_KEYS or OpCodes.MATCH_CLASS)
            {
                return true;
            }
        }
        return false;
    }

    private bool MatchShapeIsAdvanced(int idx, int endIdx)
    {
        try
        {
            if (!TryParseCaseV2(idx, endIdx, out var pat, out _, out _, out _)) return false;
            return PatNodeIsAdvanced(pat, 0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PatBindsName(PatternNode p) => p switch
    {
        MatchAsPattern a => (a.Name != null && a.Name != "_") || (a.Pattern != null && PatBindsName(a.Pattern)),
        MatchStarPattern st => st.Name != "_",
        MatchSequencePattern s => s.Patterns.Any(PatBindsName),
        MatchMappingPattern m => m.Rest != null || m.Entries.Any(e => PatBindsName(e.Pattern)),
        MatchClassPattern c => c.Patterns.Any(PatBindsName),
        MatchKeywordPattern k => PatBindsName(k.Pattern),
        MatchOrPattern o => o.Patterns.Any(PatBindsName),
        _ => false
    };

    private static bool PatNodeIsAdvanced(PatternNode p, int depth) => p switch
    {
        MatchStarPattern => true,
        MatchMappingPattern m => m.Rest != null || m.Entries.Any(e => PatNodeIsAdvanced(e.Pattern, depth + 1)) || depth > 0,
        MatchSequencePattern s => s.Patterns.Any(x => PatNodeIsAdvanced(x, depth + 1)) ||
            s.Patterns.Any(x => x is MatchSequencePattern or MatchClassPattern or MatchMappingPattern),
        MatchClassPattern c => c.Patterns.Any(x => PatNodeIsAdvanced(x, depth + 1)) ||
            c.Patterns.Any(x => x is MatchClassPattern or MatchSequencePattern or MatchMappingPattern),
        MatchOrPattern o => o.Patterns.Any(PatBindsName) ||
            o.Patterns.Any(x => x is MatchAsPattern or MatchStarPattern || PatNodeIsAdvanced(x, depth + 1)),
        MatchAsPattern a => a.Pattern switch
        {
            MatchOrPattern or MatchSequencePattern or MatchMappingPattern => true,
            MatchClassPattern c => c.Patterns.Any(x => PatNodeIsAdvanced(x, depth + 1)),
            null => false,
            _ => PatNodeIsAdvanced(a.Pattern, depth + 1)
        },
        _ => false
    };

    private bool TryDecompileMatchStmtLegacy(int idx, int endIdx, SymbolicStack stack, out StmtNode matchStmt, out int nextIdx)
    {
        matchStmt = null!;
        nextIdx = idx;

        if (stack.Count == 0) return false;

        var ins = _instrs[idx];
        if (ins.OpCode != OpCodes.COPY || (ins.OpArg ?? 0) != 1) return false;

        if (idx + 1 >= endIdx) return false;
        var nextOp = _instrs[idx + 1].OpCode;
        var isMatchHeader = nextOp is OpCodes.MATCH_SEQUENCE or OpCodes.MATCH_MAPPING or OpCodes.MATCH_KEYS or OpCodes.COPY ||
            (nextOp is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME && idx + 3 < endIdx && _instrs[idx + 3].OpCode == OpCodes.MATCH_CLASS) ||
            (nextOp is OpCodes.LOAD_CONST or OpCodes.LOAD_NAME or OpCodes.LOAD_GLOBAL && idx + 2 < endIdx && _instrs[idx + 2].OpCode is OpCodes.IS_OP or OpCodes.COMPARE_OP);
        if (!isMatchHeader) return false;

        var subject = stack.Pop();
        var cases = new List<MatchCase>();
        var curIdx = idx;
        var matchExitOffset = -1;


        var lastCaseEndIdx = -1;

        while (curIdx < endIdx)
        {
            var loopStartIdx = curIdx;
            while (curIdx < endIdx && _instrs[curIdx].OpCode == OpCodes.POP_TOP)
            {
                curIdx++;
            }

            if (curIdx >= endIdx) break;

            var curIns = _instrs[curIdx];

            if (curIns.OpCode == OpCodes.NOP)
            {
                var bodyLimit = matchExitOffset >= 0 && _offsetToIndex.TryGetValue(matchExitOffset, out var meWild) ? Math.Min(endIdx, meWild) : endIdx;
                var body = DecompileRange(curIdx + 1, bodyLimit, stack.Clone());
                cases.Add(new MatchCase(new MatchWildcardPattern(), null, body));
                lastCaseEndIdx = bodyLimit;
                break;
            }

            if (curIns.OpCode == OpCodes.COPY && curIdx + 1 < endIdx && _instrs[curIdx + 1].OpCode == OpCodes.COPY)
            {
                var orPatterns = new List<PatternNode>();
                var orIdx = curIdx;
                var bodyStartIdx = -1;
                var bodyEndIdx = -1;
                var nextCaseOffset = -1;

                while (orIdx < endIdx && _instrs[orIdx].OpCode == OpCodes.COPY)
                {
                    if (orIdx + 3 < endIdx && _instrs[orIdx + 1].OpCode == OpCodes.LOAD_CONST && _instrs[orIdx + 2].OpCode == OpCodes.COMPARE_OP && _instrs[orIdx + 3].OpCode == OpCodes.POP_JUMP_IF_FALSE)
                    {
                        var val = _instrs[orIdx + 1].ArgVal;
                        orPatterns.Add(new MatchValuePattern(new ConstantExpr(val)));
                        var jumpTarget = _instrs[orIdx + 3].TargetOffset ?? 0;
                        nextCaseOffset = jumpTarget;

                        if (bodyStartIdx == -1)
                        {
                            var s = orIdx + 4;
                            while (s < endIdx && _instrs[s].OpCode == OpCodes.POP_TOP) s++;
                            bodyStartIdx = s;
                            var e = s;
                            while (e < endIdx && _instrs[e].OpCode is not (OpCodes.RETURN_CONST or OpCodes.RETURN_VALUE)) e++;
                            if (e < endIdx) e++;
                            bodyEndIdx = e;
                        }

                        if (_offsetToIndex.TryGetValue(jumpTarget, out var jIdx) && jIdx < endIdx && _instrs[jIdx].OpCode == OpCodes.COPY)
                        {
                            orIdx = jIdx;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else if (orIdx + 4 < endIdx && _instrs[orIdx + 1].OpCode == OpCodes.COPY && _instrs[orIdx + 2].OpCode == OpCodes.LOAD_CONST && _instrs[orIdx + 3].OpCode == OpCodes.COMPARE_OP && _instrs[orIdx + 4].OpCode == OpCodes.POP_JUMP_IF_FALSE)
                    {
                        var val = _instrs[orIdx + 2].ArgVal;
                        orPatterns.Add(new MatchValuePattern(new ConstantExpr(val)));
                        var jumpTarget = _instrs[orIdx + 4].TargetOffset ?? 0;
                        nextCaseOffset = jumpTarget;

                        if (bodyStartIdx == -1)
                        {
                            var s = orIdx + 5;
                            while (s < endIdx && _instrs[s].OpCode == OpCodes.POP_TOP) s++;
                            bodyStartIdx = s;
                            var e = s;
                            while (e < endIdx && _instrs[e].OpCode is not (OpCodes.RETURN_CONST or OpCodes.RETURN_VALUE)) e++;
                            if (e < endIdx) e++;
                            bodyEndIdx = e;
                        }

                        if (_offsetToIndex.TryGetValue(jumpTarget, out var jIdx) && jIdx < endIdx && _instrs[jIdx].OpCode == OpCodes.COPY)
                        {
                            orIdx = jIdx;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                if (orPatterns.Count > 0)
                {
                    PatternNode pat = orPatterns.Count == 1 ? orPatterns[0] : new MatchOrPattern(orPatterns);
                    var body = bodyStartIdx >= 0 && bodyEndIdx >= bodyStartIdx
                        ? DecompileRange(bodyStartIdx, bodyEndIdx, stack.Clone())
                        : [];
                    cases.Add(new MatchCase(pat, null, body));
                    if (bodyEndIdx >= 0) lastCaseEndIdx = bodyEndIdx;

                    if (nextCaseOffset >= 0 && _offsetToIndex.TryGetValue(nextCaseOffset, out var nxtIdx))
                    {
                        curIdx = Math.Max(loopStartIdx + 1, nxtIdx);
                    }
                    else
                    {
                        break;
                    }
                    continue;
                }
            }

            if (curIns.OpCode == OpCodes.COPY && curIdx + 3 < endIdx &&
                _instrs[curIdx + 1].OpCode is OpCodes.LOAD_CONST or OpCodes.LOAD_NAME or OpCodes.LOAD_GLOBAL &&
                _instrs[curIdx + 2].OpCode is OpCodes.IS_OP or OpCodes.COMPARE_OP &&
                _instrs[curIdx + 3].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
            {
                var val = _instrs[curIdx + 1].ArgVal;
                var failTarget = _instrs[curIdx + 3].TargetOffset ?? 0;
                var bodyStart = curIdx + 4;
                while (bodyStart < endIdx && _instrs[bodyStart].OpCode == OpCodes.POP_TOP) bodyStart++;

                var bodyEnd = _offsetToIndex.TryGetValue(failTarget, out var fIdx) ? fIdx : endIdx;
                if (bodyEnd > bodyStart && _instrs[bodyEnd - 1].OpCode == OpCodes.JUMP_FORWARD)
                {
                    var jTarget = _instrs[bodyEnd - 1].TargetOffset ?? -1;
                    if (jTarget >= failTarget && !_activeLoopExits.Contains(jTarget))
                    {
                        matchExitOffset = matchExitOffset < 0 ? jTarget : Math.Min(matchExitOffset, jTarget);
                        bodyEnd--;
                    }
                }

                var pat = new MatchValuePattern(new ConstantExpr(val));
                var body = DecompileRange(bodyStart, bodyEnd, stack.Clone());
                cases.Add(new MatchCase(pat, null, body));
                lastCaseEndIdx = bodyEnd;

                if (_offsetToIndex.TryGetValue(failTarget, out var nextCaseIdx))
                {
                    curIdx = nextCaseIdx;
                }
                else
                {
                    break;
                }
                continue;
            }

            if (curIns.OpCode is OpCodes.LOAD_CONST or OpCodes.LOAD_NAME or OpCodes.LOAD_GLOBAL &&
                curIdx + 2 < endIdx &&
                _instrs[curIdx + 1].OpCode is OpCodes.IS_OP or OpCodes.COMPARE_OP &&
                _instrs[curIdx + 2].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
            {
                var val = curIns.ArgVal;
                var failTarget = _instrs[curIdx + 2].TargetOffset ?? 0;

                var bodyStart = curIdx + 3;
                while (bodyStart < endIdx && _instrs[bodyStart].OpCode == OpCodes.POP_TOP) bodyStart++;

                var bodyEnd = _offsetToIndex.TryGetValue(failTarget, out var fIdx) ? fIdx : endIdx;
                if (bodyEnd > bodyStart && _instrs[bodyEnd - 1].OpCode == OpCodes.JUMP_FORWARD)
                {
                    var jTarget = _instrs[bodyEnd - 1].TargetOffset ?? -1;
                    if (jTarget >= failTarget && !_activeLoopExits.Contains(jTarget))
                    {
                        matchExitOffset = matchExitOffset < 0 ? jTarget : Math.Min(matchExitOffset, jTarget);
                        bodyEnd--;
                    }
                }

                var pat = new MatchValuePattern(new ConstantExpr(val));
                var body = DecompileRange(bodyStart, bodyEnd, stack.Clone());
                cases.Add(new MatchCase(pat, null, body));
                lastCaseEndIdx = bodyEnd;

                if (_offsetToIndex.TryGetValue(failTarget, out var nextCaseIdx))
                {
                    curIdx = nextCaseIdx;
                }
                else
                {
                    break;
                }
                if (matchExitOffset >= 0 && _offsetToIndex.TryGetValue(matchExitOffset, out var me) && curIdx >= me)
                {
                    break;
                }
                continue;
            }

            if (curIns.OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
            {
                var varName = curIns.ArgRepr ?? curIns.ArgVal?.ToString() ?? "__pycrune_ph_x";
                var pat = new MatchAsPattern(null, varName);

                if (TryDecompileConditional(curIdx + 1, endIdx, stack.Clone(), out var condStmt, out var condNextIdx) && condStmt is IfStmt ifGuard)
                {
                    cases.Add(new MatchCase(pat, ifGuard.Condition, ifGuard.ThenBody));
                    curIdx = condNextIdx;
                    continue;
                }
                else
                {
                    var bodyLimit = matchExitOffset >= 0 && _offsetToIndex.TryGetValue(matchExitOffset, out var meAs) ? Math.Min(endIdx, meAs) : endIdx;
                    var body = DecompileRange(curIdx + 1, bodyLimit, stack.Clone());
                    cases.Add(new MatchCase(pat, null, body));
                    lastCaseEndIdx = bodyLimit;
                    break;
                }
            }

            var cScan = curIdx;
            while (cScan < endIdx && _instrs[cScan].OpCode == OpCodes.COPY && (_instrs[cScan].OpArg ?? 0) == 1) cScan++;
            var isClassPattern = (cScan + 2 < endIdx && (_instrs[cScan].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME) && _instrs[cScan + 2].OpCode == OpCodes.MATCH_CLASS);

            if (isClassPattern)
            {
                var clsIdx = cScan;
                var kwIdx = cScan + 1;
                var matchClassIdx = cScan + 2;

                var orAlts = new List<(string ClsName, int ArgCount)>();
                var altCur = curIdx;
                var commonSuccessOffset = -1;
                var nextCaseOffset = -1;

                while (altCur < endIdx)
                {
                    var scanA = altCur;
                    while (scanA < endIdx && _instrs[scanA].OpCode == OpCodes.COPY && (_instrs[scanA].OpArg ?? 0) == 1) scanA++;
                    if (scanA + 2 >= endIdx || _instrs[scanA].OpCode is not (OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME) || _instrs[scanA + 2].OpCode != OpCodes.MATCH_CLASS)
                        break;

                    var altCls = _instrs[scanA].ArgRepr ?? _instrs[scanA].ArgVal?.ToString() ?? throw new PatParseException("pattern class name missing");
                    var altCount = _instrs[scanA + 2].OpArg ?? 0;
                    orAlts.Add((altCls, altCount));

                    var jIdx = scanA + 3;
                    while (jIdx < endIdx && _instrs[jIdx].OpCode is not (OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_FALSE)) jIdx++;
                    if (jIdx >= endIdx) break;

                    var altFailTarget = _instrs[jIdx].TargetOffset ?? 0;
                    var afterJ = jIdx + 1;
                    while (afterJ < endIdx && _instrs[afterJ].OpCode == OpCodes.UNPACK_SEQUENCE) afterJ++;

                    if (afterJ < endIdx && _instrs[afterJ].OpCode == OpCodes.JUMP_FORWARD)
                    {
                        var succTarget = _instrs[afterJ].TargetOffset ?? 0;
                        if (commonSuccessOffset == -1) commonSuccessOffset = succTarget;

                        if (_offsetToIndex.TryGetValue(altFailTarget, out var fIdx))
                        {
                            var fPop = fIdx;
                            while (fPop < endIdx && _instrs[fPop].OpCode == OpCodes.POP_TOP) fPop++;
                            if (fPop < endIdx && _instrs[fPop].OpCode == OpCodes.COPY)
                            {
                                altCur = fPop;
                                continue;
                            }
                            else if (fPop < endIdx && _instrs[fPop].OpCode == OpCodes.JUMP_FORWARD)
                            {
                                nextCaseOffset = _instrs[fPop].TargetOffset ?? 0;
                                break;
                            }
                            else if (fPop < endIdx)
                            {
                                nextCaseOffset = _instrs[fPop].Offset;
                                break;
                            }
                        }
                    }
                    else
                    {
                        nextCaseOffset = altFailTarget;
                        break;
                    }
                    break;
                }

                if (orAlts.Count > 1 && commonSuccessOffset >= 0 && _offsetToIndex.TryGetValue(commonSuccessOffset, out var commIdx))
                {
                    var pIdx = commIdx;
                    var boundVars = new List<string>();
                    while (pIdx < endIdx && _instrs[pIdx].OpCode == OpCodes.POP_TOP) pIdx++;
                    while (pIdx < endIdx && _instrs[pIdx].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                    {
                        var vName = _instrs[pIdx].ArgRepr ?? _instrs[pIdx].ArgVal?.ToString() ?? "__pycrune_ph_x";
                        boundVars.Add(vName);
                        pIdx++;
                    }
                    while (pIdx < endIdx && _instrs[pIdx].OpCode == OpCodes.POP_TOP) pIdx++;

                    var bodyStartIdx = pIdx;
                    var bodyEndIdx = _offsetToIndex.TryGetValue(nextCaseOffset, out var nIdx) ? nIdx : endIdx;
                    while (bodyEndIdx > bodyStartIdx && _instrs[bodyEndIdx - 1].OpCode is OpCodes.NOP or OpCodes.POP_TOP) bodyEndIdx--;
                    if (bodyEndIdx > bodyStartIdx && _instrs[bodyEndIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                    {
                        matchExitOffset = Math.Max(matchExitOffset, _instrs[bodyEndIdx - 1].TargetOffset ?? -1);
                        bodyEndIdx--;
                    }

                    var patternList = new List<PatternNode>();
                    foreach (var alt in orAlts)
                    {
                        var classArgs = boundVars.Take(alt.ArgCount).Select(v => (PatternNode)new MatchAsPattern(null, v)).ToList();
                        patternList.Add(new MatchClassPattern(new NameExpr(alt.ClsName), classArgs));
                    }
                    PatternNode pat = patternList.Count == 1 ? patternList[0] : new MatchOrPattern(patternList);
                    if (boundVars.Count > 0 && orAlts.All(a => a.ArgCount == 0))
                    {
                        pat = new MatchAsPattern(pat, boundVars[0]);
                    }

                    ExprNode? guard = null;
                    List<StmtNode> body;
                    if (TryExtractMatchGuard(bodyStartIdx, bodyEndIdx, out var extractedGuard, out var extractedBodyIdx) && extractedGuard != null)
                    {


                        guard = extractedGuard;
                        body = DecompileRange(extractedBodyIdx, bodyEndIdx, stack.Clone());
                    }
                    else if (TryDecompileConditional(bodyStartIdx, bodyEndIdx, stack.Clone(), out var condStmt, out var condNextIdx) && condStmt is IfStmt ifGuard)
                    {
                        guard = ifGuard.Condition;
                        body = [.. ifGuard.ThenBody];
                    }
                    else
                    {
                        body = DecompileRange(bodyStartIdx, bodyEndIdx, stack.Clone());
                    }

                    cases.Add(new MatchCase(pat, guard, body));
                    if (bodyEndIdx >= 0) lastCaseEndIdx = bodyEndIdx;

                    if (nextCaseOffset >= 0 && _offsetToIndex.TryGetValue(nextCaseOffset, out var nxtIdx))
                    {
                        curIdx = Math.Max(loopStartIdx + 1, nxtIdx);
                    }
                    else
                    {
                        break;
                    }
                    continue;
                }
                else
                {
                    var clsName = _instrs[clsIdx].ArgRepr ?? _instrs[clsIdx].ArgVal?.ToString() ?? throw new PatParseException("pattern class name missing");
                    var kwNames = (_instrs[kwIdx].ArgVal as IEnumerable<object?>)?.Select(o => o?.ToString() ?? "").ToList() ?? new List<string>();
                    var posArgsCount = _instrs[matchClassIdx].OpArg ?? 0;
                    var totalExpectedArgs = posArgsCount + kwNames.Count;

                    var jumpIdx = matchClassIdx + 1;
                    while (jumpIdx < endIdx && _instrs[jumpIdx].OpCode is not (OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_FALSE)) jumpIdx++;
                    nextCaseOffset = jumpIdx < endIdx ? (_instrs[jumpIdx].TargetOffset ?? 0) : 0;
                    var pIdx = jumpIdx + 1;
                    if (pIdx < endIdx && _instrs[pIdx].OpCode == OpCodes.UNPACK_SEQUENCE) pIdx++;

                    var classArgs = new List<PatternNode>();
                    for (var ai = 0; ai < totalExpectedArgs && pIdx < endIdx; ai++)
                    {
                        var attrName = ai < posArgsCount ? null : kwNames[ai - posArgsCount];
                        PatternNode argPat;

                        if (pIdx + 2 < endIdx && (_instrs[pIdx].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME || (_instrs[pIdx].OpCode == OpCodes.COPY && _instrs[pIdx + 1].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)) &&
                            (_instrs[pIdx + 2].OpCode == OpCodes.MATCH_CLASS || (pIdx + 3 < endIdx && _instrs[pIdx + 3].OpCode == OpCodes.MATCH_CLASS)))
                        {
                            argPat = ParseClassPatternArgs(ref pIdx, endIdx);
                        }
                        else if (_instrs[pIdx].OpCode is OpCodes.MATCH_SEQUENCE or OpCodes.SWAP && (pIdx + 1 < endIdx && (_instrs[pIdx].OpCode == OpCodes.MATCH_SEQUENCE || _instrs[pIdx + 1].OpCode == OpCodes.MATCH_SEQUENCE)))
                        {
                            var seqStart = _instrs[pIdx].OpCode == OpCodes.MATCH_SEQUENCE ? pIdx : pIdx + 1;
                            var sScan = seqStart + 1;
                            var seqStores = new List<string>();
                            while (sScan < endIdx && _instrs[sScan].OpCode is not (OpCodes.RETURN_CONST or OpCodes.RETURN_VALUE or OpCodes.LOAD_FAST or OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME))
                            {
                                if (_instrs[sScan].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                                {
                                    var sn = _instrs[sScan].ArgRepr ?? _instrs[sScan].ArgVal?.ToString();
                                    if (!string.IsNullOrEmpty(sn) && !seqStores.Contains(sn)) seqStores.Add(sn);
                                }
                                sScan++;
                            }
                            if (seqStores.Count >= 2)
                            {
                                argPat = new MatchSequencePattern([
                                    new MatchAsPattern(null, seqStores[1]),
                                    new MatchStarPattern("_"),
                                    new MatchAsPattern(null, seqStores[0])
                                ], true);
                            }
                            else if (seqStores.Count == 1)
                            {
                                argPat = new MatchSequencePattern([new MatchAsPattern(null, seqStores[0])], true);
                            }
                            else
                            {
                                argPat = new MatchWildcardPattern();
                            }
                            pIdx = sScan;
                        }
                        else if (_instrs[pIdx].OpCode is OpCodes.MATCH_MAPPING or OpCodes.SWAP && (pIdx + 1 < endIdx && (_instrs[pIdx].OpCode == OpCodes.MATCH_MAPPING || _instrs[pIdx + 1].OpCode == OpCodes.MATCH_MAPPING)))
                        {
                            var mapStart = _instrs[pIdx].OpCode == OpCodes.MATCH_MAPPING ? pIdx : pIdx + 1;
                            var mapKeys = Array.Empty<object>();
                            var mkScan = mapStart;
                            while (mkScan < endIdx && _instrs[mkScan].OpCode != OpCodes.MATCH_KEYS)
                            {
                                if (_instrs[mkScan].OpCode == OpCodes.LOAD_CONST)
                                {
                                    var v = _instrs[mkScan].ArgVal;
                                    if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                                        mapKeys = je.EnumerateArray().Select(e => (object)(e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString()! : e.ToString())).ToArray();
                                    else if (v is object[] arr) mapKeys = arr;
                                    else if (v is System.Collections.IEnumerable en and not string) mapKeys = en.Cast<object>().ToArray();
                                }
                                mkScan++;
                            }
                            var mapStores = new List<string>();
                            var mScan = mkScan;
                            while (mScan < endIdx && _instrs[mScan].OpCode is not (OpCodes.RETURN_CONST or OpCodes.RETURN_VALUE or OpCodes.LOAD_FAST or OpCodes.LOAD_GLOBAL))
                            {
                                if (_instrs[mScan].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                                {
                                    var sn = _instrs[mScan].ArgRepr ?? _instrs[mScan].ArgVal?.ToString();
                                    if (!string.IsNullOrEmpty(sn) && !mapStores.Contains(sn)) mapStores.Add(sn);
                                }
                                mScan++;
                            }
                            string? restVar = null;
                            string? valVar = null;
                            string? otherVar = null;
                            if (mapStores.Count >= 3)
                            {
                                restVar = mapStores[0];
                                otherVar = mapStores[1];
                                valVar = mapStores[2];
                            }
                            else if (mapStores.Count == 2)
                            {
                                restVar = mapStores[0];
                                valVar = mapStores[1];
                            }
                            else if (mapStores.Count == 1)
                            {
                                valVar = mapStores[0];
                            }
                            if (otherVar != null)
                            {
                                if (classArgs.Count > 0 && classArgs[^1] is MatchWildcardPattern)
                                {
                                    classArgs[^1] = new MatchAsPattern(null, otherVar);
                                }
                                else if (classArgs.Count == 0)
                                {
                                    classArgs.Add(new MatchAsPattern(null, otherVar));
                                    ai++;
                                }
                            }
                            var mapEntries = new List<(ExprNode, PatternNode)>();
                            if (mapKeys.Length > 0)
                            {
                                var mapKey = mapKeys[0] ??
                                    throw new PatParseException("mapping key missing");
                                var valuePattern = valVar != null
                                    ? (PatternNode)new MatchAsPattern(null, valVar)
                                    : new MatchWildcardPattern();
                                mapEntries.Add((new ConstantExpr(mapKey), valuePattern));
                            }
                            PatternNode? mapRest = restVar != null ? new MatchStarPattern(restVar) : null;
                            argPat = new MatchMappingPattern(mapEntries, mapRest);
                            pIdx = mScan;
                        }
                        else if (_instrs[pIdx].OpCode == OpCodes.LOAD_CONST)
                        {
                            var cVal = _instrs[pIdx].ArgVal;
                            argPat = new MatchValuePattern(new ConstantExpr(cVal));
                            pIdx++;
                            while (pIdx < endIdx && _instrs[pIdx].OpCode is OpCodes.COMPARE_OP or OpCodes.POP_JUMP_IF_FALSE) pIdx++;
                        }
                        else if (_instrs[pIdx].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                        {
                            var vName = _instrs[pIdx].ArgRepr ?? _instrs[pIdx].ArgVal?.ToString() ?? "__pycrune_ph_x";
                            argPat = new MatchAsPattern(null, vName);
                            pIdx++;
                        }
                        else if (_instrs[pIdx].OpCode == OpCodes.POP_TOP)
                        {
                            argPat = new MatchWildcardPattern();
                            pIdx++;
                        }
                        else
                        {
                            argPat = new MatchWildcardPattern();
                            pIdx++;
                        }

                        if (!string.IsNullOrEmpty(attrName))
                        {
                            argPat = new MatchKeywordPattern(attrName, argPat);
                        }
                        classArgs.Add(argPat);
                    }

                    var patternStores = new List<string>();
                    while (pIdx < endIdx)
                    {
                        if (_instrs[pIdx].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                        {
                            patternStores.Add(_instrs[pIdx].ArgRepr ?? _instrs[pIdx].ArgVal?.ToString() ?? "__pycrune_ph_x");
                            pIdx++;
                        }
                        else if (_instrs[pIdx].OpCode is OpCodes.POP_TOP or OpCodes.SWAP)
                        {
                            pIdx++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    string? asCapture = null;
                    if (patternStores.Count > 0)
                    {
                        var sIdx = 0;
                        for (var ai = 0; ai < classArgs.Count && sIdx < patternStores.Count; ai++)
                        {
                            var arg = classArgs[ai];
                            if (arg is MatchKeywordPattern kwPat && kwPat.Pattern is MatchWildcardPattern)
                            {
                                classArgs[ai] = new MatchKeywordPattern(kwPat.Attribute, new MatchAsPattern(null, patternStores[sIdx++]));
                            }
                            else if (arg is MatchWildcardPattern)
                            {
                                classArgs[ai] = new MatchAsPattern(null, patternStores[sIdx++]);
                            }
                        }
                        if (sIdx < patternStores.Count) asCapture = patternStores[sIdx];
                    }

                    ExprNode? guard = null;
                    var curCaseLimit = _offsetToIndex.TryGetValue(nextCaseOffset, out var ncoIdx) ? ncoIdx : endIdx;
                    var guardStart = pIdx;
                    var guardEnd = -1;
                    for (var g = curCaseLimit - 1; g >= guardStart; g--)
                    {
                        if (_instrs[g].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
                        {
                            var tgt = _instrs[g].TargetOffset ?? -1;
                            if (tgt >= nextCaseOffset)
                            {
                                guardEnd = g;
                                nextCaseOffset = tgt;
                                break;
                            }
                        }
                    }

                    if (guardEnd > guardStart)
                    {
                        var gStack = new SymbolicStack();
                        DecompileRange(guardStart, guardEnd, gStack);
                        if (gStack.Count > 0) guard = gStack.Pop();
                        pIdx = guardEnd + 1;
                        while (pIdx < endIdx && _instrs[pIdx].OpCode == OpCodes.POP_TOP) pIdx++;
                    }

                    var bodyStartIdx = pIdx;
                    var bodyEndIdx = _offsetToIndex.TryGetValue(nextCaseOffset, out var nIdx) ? nIdx : endIdx;
                    while (bodyEndIdx > bodyStartIdx && _instrs[bodyEndIdx - 1].OpCode is OpCodes.NOP or OpCodes.POP_TOP) bodyEndIdx--;
                    if (bodyEndIdx > bodyStartIdx && _instrs[bodyEndIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                    {
                        matchExitOffset = Math.Max(matchExitOffset, _instrs[bodyEndIdx - 1].TargetOffset ?? -1);
                        bodyEndIdx--;
                    }

                    var body = DecompileRange(bodyStartIdx, bodyEndIdx, new SymbolicStack());
                    PatternNode classPattern = new MatchClassPattern(new NameExpr(clsName), classArgs);
                    if (asCapture != null) classPattern = new MatchAsPattern(classPattern, asCapture);
                    cases.Add(new MatchCase(classPattern, guard, body));
                    if (bodyEndIdx >= 0) lastCaseEndIdx = bodyEndIdx;

                    if (nextCaseOffset >= 0 && _offsetToIndex.TryGetValue(nextCaseOffset, out var nxtIdx))
                    {
                        curIdx = Math.Max(loopStartIdx + 1, nxtIdx);
                    }
                    else
                    {
                        break;
                    }
                    continue;
                }
            }

            if (curIns.OpCode == OpCodes.MATCH_SEQUENCE || (curIns.OpCode == OpCodes.COPY && curIdx + 1 < endIdx && _instrs[curIdx + 1].OpCode == OpCodes.MATCH_SEQUENCE))
            {
                var seqStartIdx = curIns.OpCode == OpCodes.MATCH_SEQUENCE ? curIdx : curIdx + 1;
                var failTargets = new List<int>();
                var unpackIdx = -1;
                var isUnpackEx = false;
                var unpackArg = 0;

                for (var s = seqStartIdx; s < endIdx; s++)
                {
                    if (_instrs[s].TargetOffset is int to1 && _instrs[s].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_NONE)
                    {
                        failTargets.Add(to1);
                    }
                    if (_instrs[s].OpCode == OpCodes.UNPACK_SEQUENCE)
                    {
                        unpackIdx = s;
                        unpackArg = _instrs[s].OpArg ?? 0;
                        break;
                    }
                    if (_instrs[s].OpCode == OpCodes.UNPACK_EX)
                    {
                        unpackIdx = s;
                        isUnpackEx = true;
                        unpackArg = _instrs[s].OpArg ?? 0;
                        break;
                    }
                }

                if (unpackIdx < 0)
                {


                    var failTarget = failTargets.Count > 0 ? failTargets[0] : 0;
                    var bodyLimit = _offsetToIndex.TryGetValue(failTarget, out var ftIdx) ? ftIdx : endIdx;
                    var sawFirst = false;
                    var sawLast = false;
                    var stores = new List<string>();
                    var lastStore = -1;
                    var scanStores = false;
                    for (var s = seqStartIdx; s < bodyLimit; s++)
                    {
                        if (_instrs[s].OpCode == OpCodes.LOAD_CONST &&
                            s + 1 < bodyLimit && _instrs[s + 1].OpCode == OpCodes.BINARY_SUBSCR &&
                            IsIntegerValue(_instrs[s].ArgVal) && ToBigInteger(_instrs[s].ArgVal!) == 0)
                        {
                            sawFirst = true;
                        }
                        if (_instrs[s].OpCode == OpCodes.GET_LEN &&
                            s + 3 < bodyLimit &&
                            _instrs[s + 1].OpCode == OpCodes.LOAD_CONST &&
                            _instrs[s + 2].OpCode == OpCodes.BINARY_OP &&
                            _instrs[s + 3].OpCode == OpCodes.BINARY_SUBSCR)
                        {
                            sawLast = true;
                            scanStores = true;
                            s += 3;
                            continue;
                        }
                        if (!scanStores) continue;
                        if (_instrs[s].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                        {
                            var storeName = _instrs[s].ArgRepr ?? _instrs[s].ArgVal?.ToString();
                            if (!string.IsNullOrEmpty(storeName)) stores.Add(storeName);
                            lastStore = s;
                            continue;
                        }
                        if (_instrs[s].OpCode is OpCodes.POP_TOP or OpCodes.SWAP or OpCodes.NOP or OpCodes.NOT_TAKEN)
                        {
                            continue;
                        }
                        break;
                    }

                    if (sawFirst && sawLast && stores.Count == 2 && lastStore >= 0)
                    {
                        var pat = new MatchSequencePattern([
                            new MatchAsPattern(null, stores[1]),
                            new MatchStarPattern("_"),
                            new MatchAsPattern(null, stores[0])
                        ], true);

                        var bodyStart = lastStore + 1;
                        while (bodyStart < bodyLimit &&
                               _instrs[bodyStart].OpCode is OpCodes.POP_TOP or OpCodes.SWAP)
                        {
                            bodyStart++;
                        }

                        ExprNode? seqGuard = null;
                        List<StmtNode> seqBody;
                        if (TryExtractMatchGuard(bodyStart, endIdx, out var gExpr2, out var gBodyIdx2))
                        {
                            seqGuard = gExpr2;
                            var bLimit = bodyLimit;
                            while (bLimit > gBodyIdx2 && _instrs[bLimit - 1].OpCode is OpCodes.NOP or OpCodes.POP_TOP) bLimit--;
                            if (bLimit > gBodyIdx2 && _instrs[bLimit - 1].OpCode == OpCodes.JUMP_FORWARD) bLimit--;
                            seqBody = DecompileRange(gBodyIdx2, bLimit, stack.Clone());
                        }
                        else if (TryDecompileConditional(bodyStart, bodyLimit, stack.Clone(), out var condStmt, out _) &&
                                 condStmt is IfStmt ifGuard)
                        {
                            seqGuard = ifGuard.Condition;
                            seqBody = [.. ifGuard.ThenBody];
                        }
                        else
                        {
                            seqBody = DecompileRange(bodyStart, bodyLimit, stack.Clone());
                        }
                        cases.Add(new MatchCase(pat, seqGuard, seqBody));
                        lastCaseEndIdx = bodyLimit;

                        if (failTarget >= 0 && _offsetToIndex.TryGetValue(failTarget, out var nxtCaseIdx))
                        {
                            curIdx = Math.Max(loopStartIdx + 1, nxtCaseIdx);
                            continue;
                        }
                    }

                    curIdx = loopStartIdx + 1;
                    continue;
                }

                var beforeCount = isUnpackEx ? (unpackArg & 0xFF) : unpackArg;
                var afterCount = isUnpackEx ? (unpackArg >> 8) : 0;
                var seqLen = isUnpackEx ? beforeCount + afterCount + 1 : unpackArg;
                var starIdx = isUnpackEx ? beforeCount : -1;

                var scanIdx = unpackIdx + 1;
                var lastStoreIdx = -1;
                var lastPopTop = -1;
                var elemsSeen = 0;
                var k = scanIdx;

                while (k < endIdx)
                {
                    var insK = _instrs[k];

                    if (insK.OpCode == OpCodes.LOAD_CONST && k + 2 < endIdx && _instrs[k + 1].OpCode == OpCodes.COMPARE_OP && _instrs[k + 2].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_NONE)
                    {
                        if (_instrs[k + 2].TargetOffset is int toConst) failTargets.Add(toConst);
                        elemsSeen++;
                        k += 3;
                        continue;
                    }

                    if (insK.OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
                    {
                        var scanM = k + 1;
                        while (scanM < endIdx && _instrs[scanM].OpCode is not (OpCodes.MATCH_CLASS or OpCodes.STORE_FAST or OpCodes.STORE_NAME)) scanM++;
                        if (scanM < endIdx && _instrs[scanM].OpCode == OpCodes.MATCH_CLASS)
                        {
                            var jScan = scanM + 1;
                            while (jScan < endIdx && _instrs[jScan].OpCode is not (OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_FALSE)) jScan++;
                            if (jScan < endIdx && _instrs[jScan].TargetOffset is int toClass)
                            {
                                failTargets.Add(toClass);
                            }
                            k = jScan + 1;
                            continue;
                        }
                    }

                    if (insK.OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                    {
                        lastStoreIdx = k;
                        elemsSeen++;
                        k++;
                        continue;
                    }

                    if (insK.OpCode == OpCodes.POP_TOP && elemsSeen < seqLen)
                    {
                        elemsSeen++;
                        k++;
                        continue;
                    }

                    if (elemsSeen >= seqLen)
                    {
                        if (insK.OpCode == OpCodes.POP_TOP)
                        {
                            lastPopTop = k;
                            break;
                        }
                        if (insK.TargetOffset is int toGuard && insK.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
                        {
                            failTargets.Add(toGuard);
                            if (k + 1 < endIdx && _instrs[k + 1].OpCode == OpCodes.POP_TOP)
                            {
                                lastPopTop = k + 1;
                                break;
                            }
                        }
                        if (seqStartIdx == curIdx)
                        {
                            break;
                        }
                    }

                    k++;
                }

                var nextCaseOffset = _instrs[seqStartIdx + 1].TargetOffset ?? (seqStartIdx + 2 < endIdx ? (_instrs[seqStartIdx + 2].TargetOffset ?? 0) : 0);
                var bodyEndIdx = _offsetToIndex.TryGetValue(nextCaseOffset, out var beIdx) ? beIdx : endIdx;
                while (bodyEndIdx > scanIdx && _instrs[bodyEndIdx - 1].OpCode is OpCodes.NOP or OpCodes.POP_TOP) bodyEndIdx--;
                if (bodyEndIdx > scanIdx && _instrs[bodyEndIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                {
                    matchExitOffset = Math.Max(matchExitOffset, _instrs[bodyEndIdx - 1].TargetOffset ?? -1);
                    bodyEndIdx--;
                }

                var minGuardScan = Math.Max(scanIdx, lastStoreIdx);
                var guardJumpIdx = -1;
                for (var g = bodyEndIdx - 1; g > minGuardScan; g--)
                {
                    if (_instrs[g].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
                    {
                        var tgt = _instrs[g].TargetOffset ?? -1;
                        if (tgt >= nextCaseOffset)
                        {
                            guardJumpIdx = g;
                            break;
                        }
                    }
                }

                ExprNode? guard = null;
                int bodyStartIdx = 0;
                var guardStart = -1;

                if (guardJumpIdx >= 0)
                {
                    var lastStoreBeforeGuard = scanIdx - 1;
                    for (var s = scanIdx; s < guardJumpIdx; s++)
                    {
                        if (_instrs[s].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                        {
                            if (s > 0 && _instrs[s - 1].OpCode == OpCodes.COPY && (_instrs[s - 1].OpArg ?? 0) == 1)
                                continue;
                            lastStoreBeforeGuard = s;
                        }
                    }
                    guardStart = lastStoreBeforeGuard + 1;
                    while (guardStart < guardJumpIdx && _instrs[guardStart].OpCode is OpCodes.POP_TOP or OpCodes.SWAP) guardStart++;


                    var extractedChain = false;
                    if (guardStart >= 0 && guardStart < guardJumpIdx)
                    {
                        var firstJump = -1;
                        for (var fj = guardStart; fj <= guardJumpIdx; fj++)
                        {
                            if (_instrs[fj].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
                            {
                                firstJump = fj;
                                break;
                            }
                        }

                        if (firstJump >= 0)
                        {
                            var firstTarget = _instrs[firstJump].TargetOffset ?? -1;
                            var forward = firstTarget > _instrs[firstJump].Offset;
                            var leavesPattern = firstTarget >= nextCaseOffset;
                            if (forward && leavesPattern &&
                                TryExtractMatchGuard(guardStart, bodyEndIdx, out var chainGuard, out var chainBodyIdx) &&
                                chainGuard != null)
                            {
                                guard = chainGuard;
                                bodyStartIdx = chainBodyIdx;
                                while (bodyStartIdx < bodyEndIdx && _instrs[bodyStartIdx].OpCode == OpCodes.POP_TOP) bodyStartIdx++;
                                extractedChain = true;
                            }
                        }
                    }

                    if (!extractedChain)
                    {
                        if (guardJumpIdx > guardStart)
                        {
                            var gStack = new SymbolicStack();
                            DecompileRange(guardStart, guardJumpIdx, gStack);
                            if (gStack.Count > 0) guard = gStack.Pop();
                        }
                        bodyStartIdx = guardJumpIdx + 1;
                        while (bodyStartIdx < bodyEndIdx && _instrs[bodyStartIdx].OpCode == OpCodes.POP_TOP) bodyStartIdx++;
                    }
                }
                else
                {
                    bodyStartIdx = lastPopTop >= 0 ? lastPopTop + 1 : (lastStoreIdx >= 0 ? lastStoreIdx + 1 : scanIdx);
                }

                var finalPatterns = new List<PatternNode>();
                var elemIdx = 0;
                var p = scanIdx;
                var pLimit = guard != null && guardStart > 0 ? guardStart : bodyStartIdx;

                var hasNestedMapping = false;
                for (var s = scanIdx; s < pLimit; s++)
                {
                    if (_instrs[s].OpCode == OpCodes.MATCH_MAPPING)
                    {
                        hasNestedMapping = true;
                        break;
                    }
                }

                if (hasNestedMapping && seqLen == 2)
                {
                    var orValues = new List<PatternNode>();
                    string? asVar = null;
                    PatternNode? innerMapPat = null;

                    var s = scanIdx;
                    while (s < pLimit)
                    {
                        if (_instrs[s].OpCode == OpCodes.LOAD_CONST && s + 1 < pLimit && _instrs[s + 1].OpCode == OpCodes.COMPARE_OP && (s == 0 || _instrs[s - 1].OpCode != OpCodes.GET_LEN))
                        {
                            orValues.Add(new MatchValuePattern(new ConstantExpr(_instrs[s].ArgVal)));
                            s += 2;
                            continue;
                        }
                        if (_instrs[s].OpCode == OpCodes.MATCH_MAPPING)
                        {
                            var mkIdx = s;
                            var mapKeys = Array.Empty<object>();
                            while (mkIdx < pLimit && _instrs[mkIdx].OpCode != OpCodes.MATCH_KEYS)
                            {
                                if (_instrs[mkIdx].OpCode == OpCodes.LOAD_CONST && (mkIdx == 0 || _instrs[mkIdx - 1].OpCode != OpCodes.GET_LEN))
                                {
                                    var v = _instrs[mkIdx].ArgVal;
                                    if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                                        mapKeys = je.EnumerateArray().Select(e => (object)(e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString()! : e.ToString())).ToArray();
                                    else if (v is object[] arr) mapKeys = arr;
                                    else if (v is System.Collections.IEnumerable en and not string) mapKeys = en.Cast<object>().ToArray();
                                }
                                mkIdx++;
                            }
                            var innerStores = new List<string>();
                            for (var st = mkIdx; st < pLimit; st++)
                            {
                                if (_instrs[st].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                                    innerStores.Add(_instrs[st].ArgRepr ?? _instrs[st].ArgVal?.ToString() ?? "");
                            }
                            var mapEntries = new List<(ExprNode, PatternNode)>();
                            for (var mi = 0; mi < mapKeys.Length; mi++)
                            {
                                var vName = mi < innerStores.Count ? innerStores[mi] : "v";
                                mapEntries.Add((new ConstantExpr(mapKeys[mi]), new MatchAsPattern(null, vName)));
                            }
                            innerMapPat = new MatchMappingPattern(mapEntries, null);
                            if (innerStores.Count > mapKeys.Length)
                            {
                                asVar = innerStores[^1];
                            }
                            break;
                        }
                        s++;
                    }

                    PatternNode firstPat = orValues.Count > 1 ? new MatchOrPattern(orValues) : (orValues.Count == 1 ? orValues[0] : new MatchWildcardPattern());
                    if (!string.IsNullOrEmpty(asVar))
                    {
                        firstPat = new MatchAsPattern(firstPat, asVar);
                    }
                    finalPatterns.Add(firstPat);
                    if (innerMapPat != null)
                    {
                        finalPatterns.Add(innerMapPat);
                    }
                }
                else
                {
                    var classList = new List<string>();
                    for (var sc = p; sc < pLimit; sc++)
                    {
                        if (_instrs[sc].OpCode == OpCodes.MATCH_CLASS)
                        {
                            var cName = "object";
                            for (var b = sc - 1; b >= p; b--)
                            {
                                if (_instrs[b].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
                                {
                                    cName = _instrs[b].ArgRepr ?? _instrs[b].ArgVal?.ToString() ?? throw new PatParseException("pattern class name missing");
                                    break;
                                }
                            }
                            classList.Add(cName);
                        }
                    }
                    var seqStores = new List<string>();
                    for (var sc = p; sc < pLimit; sc++)
                    {
                        if (_instrs[sc].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                        {
                            var sName = _instrs[sc].ArgRepr ?? _instrs[sc].ArgVal?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(sName) && !seqStores.Contains(sName))
                                seqStores.Add(sName);
                        }
                    }

                    if (classList.Count > 0 && classList.Count == seqLen && seqStores.Count == seqLen)
                    {
                        for (var i = 0; i < seqLen; i++)
                        {
                            finalPatterns.Add(new MatchClassPattern(new NameExpr(classList[i]), [new MatchAsPattern(null, seqStores[seqLen - 1 - i])]));
                        }
                        elemIdx = seqLen;
                        p = pLimit;
                    }

                    while (p < pLimit && elemIdx < seqLen)
                    {
                        if (_instrs[p].OpCode == OpCodes.SWAP)
                        {
                            p++;
                            continue;
                        }

                        if (_instrs[p].OpCode == OpCodes.POP_TOP)
                        {
                            if (elemIdx == starIdx)
                                finalPatterns.Add(new MatchStarPattern("_"));
                            else
                                finalPatterns.Add(new MatchWildcardPattern());
                            elemIdx++;
                            p++;
                            continue;
                        }

                        if (_instrs[p].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                        {
                            var vName = _instrs[p].ArgRepr ?? _instrs[p].ArgVal?.ToString() ?? "__pycrune_ph_x";
                            if (elemIdx == starIdx)
                                finalPatterns.Add(new MatchStarPattern(vName));
                            else
                                finalPatterns.Add(new MatchAsPattern(null, vName));
                            elemIdx++;
                            p++;
                            continue;
                        }

                        if (_instrs[p].OpCode == OpCodes.LOAD_CONST && p + 1 < pLimit && _instrs[p + 1].OpCode == OpCodes.COMPARE_OP && (p == 0 || _instrs[p - 1].OpCode != OpCodes.GET_LEN))
                        {
                            finalPatterns.Add(new MatchValuePattern(new ConstantExpr(_instrs[p].ArgVal)));
                            elemIdx++;
                            p += 2;
                            while (p < pLimit && _instrs[p].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_NONE) p++;
                            continue;
                        }

                        var pCheck = p;
                        while (pCheck < pLimit && _instrs[pCheck].OpCode == OpCodes.COPY) pCheck++;
                        if (pCheck + 2 < pLimit && (_instrs[pCheck].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME) && _instrs[pCheck + 2].OpCode == OpCodes.MATCH_CLASS)
                        {
                            var orClassPats = new List<PatternNode>();
                            var commonSucc = -1;
                            var curAlt = p;
                            while (curAlt < pLimit)
                            {
                                var aScan = curAlt;
                                while (aScan < pLimit && _instrs[aScan].OpCode == OpCodes.COPY) aScan++;
                                if (aScan + 2 >= pLimit || _instrs[aScan].OpCode is not (OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME) || _instrs[aScan + 2].OpCode != OpCodes.MATCH_CLASS)
                                    break;
                                var pArg = aScan;
                                var parsedPat = ParseClassPatternArgs(ref pArg, pLimit);
                                orClassPats.Add(parsedPat);

                                if (pArg < pLimit && _instrs[pArg].OpCode == OpCodes.JUMP_FORWARD)
                                {
                                    var sTgt = _instrs[pArg].TargetOffset ?? 0;
                                    if (commonSucc == -1) commonSucc = sTgt;
                                    pArg++;
                                }
                                while (pArg < pLimit && _instrs[pArg].OpCode == OpCodes.POP_TOP) pArg++;
                                if (pArg < pLimit && _instrs[pArg].OpCode == OpCodes.COPY && commonSucc >= 0)
                                {
                                    curAlt = pArg;
                                    continue;
                                }
                                curAlt = pArg;
                                break;
                            }

                            if (orClassPats.Count > 1 && commonSucc >= 0 && _offsetToIndex.TryGetValue(commonSucc, out var csIdx))
                            {
                                finalPatterns.Add(new MatchOrPattern(orClassPats));
                                elemIdx++;
                                p = csIdx;
                                while (p < pLimit && _instrs[p].OpCode == OpCodes.POP_TOP) p++;
                                continue;
                            }
                            else if (orClassPats.Count == 1)
                            {
                                finalPatterns.Add(orClassPats[0]);
                                elemIdx++;
                                p = curAlt;
                                while (p < pLimit && _instrs[p].OpCode is OpCodes.POP_TOP or OpCodes.COPY) p++;
                                continue;
                            }
                        }

                        p++;
                    }

                    while (elemIdx < seqLen)
                    {
                        if (elemIdx == starIdx)
                            finalPatterns.Add(new MatchStarPattern("args"));
                        else
                            finalPatterns.Add(new MatchWildcardPattern());
                        elemIdx++;
                    }
                }

                var body = DecompileRange(bodyStartIdx, bodyEndIdx, stack.Clone());
                cases.Add(new MatchCase(new MatchSequencePattern(finalPatterns, true), guard, body));
                if (bodyEndIdx >= 0) lastCaseEndIdx = bodyEndIdx;

                if (nextCaseOffset >= 0 && _offsetToIndex.TryGetValue(nextCaseOffset, out var nxtIdx))
                {
                    curIdx = Math.Max(loopStartIdx + 1, nxtIdx);
                }
                else
                {
                    break;
                }
                continue;
            }

            if (curIns.OpCode is OpCodes.MATCH_MAPPING || (curIns.OpCode == OpCodes.COPY && curIdx + 1 < endIdx && _instrs[curIdx + 1].OpCode == OpCodes.MATCH_MAPPING))
            {
                var mapStart = curIns.OpCode == OpCodes.MATCH_MAPPING ? curIdx : curIdx + 1;
                var nextCaseOffset = _instrs[mapStart + 1].TargetOffset ?? 0;
                var keysTuple = Array.Empty<object>();
                var keysIdx = mapStart;
                while (keysIdx < endIdx && _instrs[keysIdx].OpCode != OpCodes.MATCH_KEYS)
                {
                    if (_instrs[keysIdx].OpCode == OpCodes.LOAD_CONST)
                    {
                        var v = _instrs[keysIdx].ArgVal;
                        if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            keysTuple = je.EnumerateArray().Select(e => (object)(e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString()! : e.ToString())).ToArray();
                        }
                        else if (v is object[] arr)
                        {
                            keysTuple = arr;
                        }
                        else if (v is System.Collections.IEnumerable en and not string)
                        {
                            keysTuple = en.Cast<object>().ToArray();
                        }
                    }
                    keysIdx++;
                }

                var entries = new List<(ExprNode Key, PatternNode Pattern)>();
                var mIdx = keysIdx + 1;
                while (mIdx < endIdx && _instrs[mIdx].OpCode is OpCodes.COPY or OpCodes.POP_JUMP_IF_NONE or OpCodes.UNPACK_SEQUENCE) mIdx++;

                var bodyEndIdx = _offsetToIndex.TryGetValue(nextCaseOffset, out var nxtCaseIdx) ? nxtCaseIdx : endIdx;

                var patternCheckLimit = bodyEndIdx;
                for (var sc = mIdx; sc < bodyEndIdx; sc++)
                {
                    if (_instrs[sc].OpCode is OpCodes.BUILD_MAP or OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                    {
                        patternCheckLimit = sc;
                        break;
                    }
                }

                var storesInCase = new List<string>();
                for (var sc = mIdx; sc < bodyEndIdx; sc++)
                {
                    if (_instrs[sc].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                    {
                        if (sc > 0 && _instrs[sc - 1].OpCode == OpCodes.COPY && (_instrs[sc - 1].OpArg ?? 0) == 1)
                            continue;
                        var sn = _instrs[sc].ArgRepr ?? _instrs[sc].ArgVal?.ToString();
                        if (!string.IsNullOrEmpty(sn) && !storesInCase.Contains(sn))
                            storesInCase.Add(sn);
                    }
                }

                var constChecks = new List<object?>();
                for (var sc = mIdx; sc < patternCheckLimit; sc++)
                {
                    if (_instrs[sc].OpCode == OpCodes.LOAD_CONST && sc + 1 < bodyEndIdx && _instrs[sc + 1].OpCode == OpCodes.COMPARE_OP && (sc == 0 || _instrs[sc - 1].OpCode != OpCodes.GET_LEN))
                    {
                        constChecks.Add(_instrs[sc].ArgVal);
                    }
                }

                var buildMapCount = 0;
                for (var sc = mIdx; sc < bodyEndIdx; sc++)
                {
                    if (_instrs[sc].OpCode == OpCodes.BUILD_MAP)
                        buildMapCount++;
                }

                PatternNode? restPattern = null;
                var usedStores = new HashSet<string>();

                var hasNestedMap = false;
                var innerMapKeys = Array.Empty<object>();
                for (var sc = mIdx; sc < bodyEndIdx; sc++)
                {
                    if (_instrs[sc].OpCode == OpCodes.MATCH_MAPPING)
                    {
                        hasNestedMap = true;
                        var ikIdx = sc;
                        while (ikIdx < bodyEndIdx && _instrs[ikIdx].OpCode != OpCodes.MATCH_KEYS)
                        {
                            if (_instrs[ikIdx].OpCode == OpCodes.LOAD_CONST && (ikIdx == 0 || _instrs[ikIdx - 1].OpCode != OpCodes.GET_LEN))
                            {
                                var v = _instrs[ikIdx].ArgVal;
                                if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    innerMapKeys = je.EnumerateArray().Select(e => (object)(e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString()! : e.ToString())).ToArray();
                                else if (v is object[] arr) innerMapKeys = arr;
                                else if (v is System.Collections.IEnumerable en and not string) innerMapKeys = en.Cast<object>().ToArray();
                            }
                            ikIdx++;
                        }
                        break;
                    }
                }

                var hasNestedSeq = false;
                var seqUnpackEx = false;
                var seqCounts = 0;
                var seqCount = 0;
                for (var sc = mIdx; sc < bodyEndIdx; sc++)
                {
                    if (_instrs[sc].OpCode == OpCodes.MATCH_SEQUENCE)
                    {
                        hasNestedSeq = true;
                        for (var u = sc + 1; u < bodyEndIdx; u++)
                        {
                            if (_instrs[u].OpCode == OpCodes.UNPACK_EX)
                            {
                                seqUnpackEx = true;
                                seqCounts = _instrs[u].OpArg ?? 0;
                                break;
                            }
                            if (_instrs[u].OpCode == OpCodes.UNPACK_SEQUENCE)
                            {
                                seqCount = _instrs[u].OpArg ?? 0;
                                break;
                            }
                        }
                        break;
                    }
                }

                PatternNode? innerRestPattern = null;
                if (hasNestedMap)
                {
                    if (buildMapCount >= 2 && storesInCase.Count >= 3)
                    {
                        innerRestPattern = new MatchStarPattern(storesInCase[^1]);
                        usedStores.Add(storesInCase[^1]);
                        restPattern = new MatchStarPattern(storesInCase[0]);
                        usedStores.Add(storesInCase[0]);
                    }
                    else if (buildMapCount == 1 && storesInCase.Count > 0)
                    {
                        var remainingRest = storesInCase[^1];
                        innerRestPattern = new MatchStarPattern(remainingRest);
                        usedStores.Add(remainingRest);
                    }
                }
                else if (buildMapCount >= 1 && storesInCase.Count > 0)
                {
                    restPattern = new MatchStarPattern(storesInCase[0]);
                    usedStores.Add(storesInCase[0]);
                }

                var classChecks = new List<(string Name, int Args, string ArgVar)>();
                for (var sc = mIdx; sc < patternCheckLimit; sc++)
                {
                    if (_instrs[sc].OpCode == OpCodes.MATCH_CLASS)
                    {
                        var clsName = "object";
                        for (var b = sc - 1; b >= mIdx; b--)
                        {
                            if (_instrs[b].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)
                            {
                                clsName = _instrs[b].ArgRepr ?? _instrs[b].ArgVal?.ToString() ?? throw new PatParseException("pattern class name missing");
                                break;
                            }
                        }
                        var argVar = "";
                        for (var st = sc + 1; st < bodyEndIdx; st++)
                        {
                            if (_instrs[st].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
                            {
                                var sName = _instrs[st].ArgRepr ?? _instrs[st].ArgVal?.ToString() ?? "";
                                if (!usedStores.Contains(sName))
                                {
                                    argVar = sName;
                                    break;
                                }
                            }
                        }
                        classChecks.Add((clsName, _instrs[sc].OpArg ?? 0, argVar));
                    }
                }


                int CountValueChecksBefore(int target)
                {
                    var n = 0;
                    for (var sc = mIdx; sc < target && sc < bodyEndIdx; sc++)
                    {
                        if (_instrs[sc].OpCode == OpCodes.COMPARE_OP &&
                            sc + 1 < bodyEndIdx &&
                            _instrs[sc + 1].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE &&
                            (_instrs[sc].OpArg ?? 0) == 40)
                        {
                            n++;
                        }
                    }
                    return n;
                }

                var seqPos = -1;
                var mapPos = -1;
                for (var sc = mIdx; sc < bodyEndIdx; sc++)
                {
                    if (seqPos < 0 && _instrs[sc].OpCode == OpCodes.MATCH_SEQUENCE) seqPos = CountValueChecksBefore(sc);
                    if (mapPos < 0 && _instrs[sc].OpCode == OpCodes.MATCH_MAPPING) mapPos = CountValueChecksBefore(sc);
                }

                var keyPatterns = new Dictionary<string, PatternNode>();
                var constIdx = 0;
                var classIdx = 0;
                var keyIdx = -1;

                foreach (var kObj in keysTuple)
                {
                    keyIdx++;
                    var kStr = kObj?.ToString() ?? "";
                    if (hasNestedMap && innerMapKeys.Length > 0 && !keyPatterns.ContainsKey(kStr) && (mapPos < 0 || keyIdx == mapPos) && (!hasNestedSeq || seqPos >= 0 || constIdx < constChecks.Count || keysTuple.Length == 1))
                    {
                        var innerEntries = new List<(ExprNode, PatternNode)>();
                        foreach (var ikObj in innerMapKeys)
                        {
                            var ikStr = ikObj?.ToString() ?? "";
                            if (constIdx < constChecks.Count)
                            {
                                innerEntries.Add((new ConstantExpr(ikStr), new MatchValuePattern(new ConstantExpr(constChecks[constIdx++]))));
                            }
                            else
                            {
                                var matchingStore = storesInCase.FirstOrDefault(s => !usedStores.Contains(s)) ?? "__pycrune_ph_token";
                                usedStores.Add(matchingStore);
                                innerEntries.Add((new ConstantExpr(ikStr), new MatchAsPattern(null, matchingStore)));
                            }
                        }
                        keyPatterns[kStr] = new MatchMappingPattern(innerEntries, innerRestPattern);
                    }
                    else if (hasNestedSeq && !keyPatterns.ContainsKey(kStr) && (seqPos < 0 || keyIdx == seqPos))
                    {
                        var seqStores = storesInCase.Where(s => !usedStores.Contains(s)).ToList();
                        if (seqUnpackEx)
                        {
                            var leading = seqCounts & 0xFF;
                            var trailing = seqCounts >> 8;
                            var seqPats = new List<PatternNode>();
                            var sIdx = 0;
                            if (leading > 0 && sIdx < seqStores.Count)
                            {
                                if (classChecks.Count > 0)
                                {
                                    var cls = classChecks[0];
                                    var clsArgs = new List<PatternNode>();
                                    for (var ci = 0; ci < cls.Args && sIdx < seqStores.Count; ci++)
                                    {
                                        clsArgs.Add(new MatchAsPattern(null, seqStores[sIdx]));
                                        usedStores.Add(seqStores[sIdx++]);
                                    }
                                    seqPats.Add(new MatchClassPattern(new NameExpr(cls.Name), clsArgs));
                                }
                                else
                                {
                                    seqPats.Add(new MatchAsPattern(null, seqStores[sIdx]));
                                    usedStores.Add(seqStores[sIdx++]);
                                }
                            }
                            if (sIdx < seqStores.Count)
                            {
                                seqPats.Add(new MatchStarPattern(seqStores[sIdx]));
                                usedStores.Add(seqStores[sIdx++]);
                            }
                            if (trailing > 0 && sIdx < seqStores.Count)
                            {
                                seqPats.Add(new MatchAsPattern(null, seqStores[sIdx]));
                                usedStores.Add(seqStores[sIdx++]);
                            }
                            keyPatterns[kStr] = new MatchSequencePattern(seqPats, false);
                        }
                        else
                        {
                            var seqPats = new List<PatternNode>();
                            for (var si = 0; si < seqCount && si < seqStores.Count; si++)
                            {
                                seqPats.Add(new MatchAsPattern(null, seqStores[si]));
                                usedStores.Add(seqStores[si]);
                            }
                            keyPatterns[kStr] = new MatchSequencePattern(seqPats, true);
                        }
                        hasNestedSeq = false;
                    }
                    else if (constIdx < constChecks.Count)
                    {
                        var remainingKeys = keysTuple.Where(k => !keyPatterns.ContainsKey(k?.ToString() ?? "")).Count();
                        var hasStoreForOtherKey = storesInCase.Count > 0 && keysTuple.Any(k => (k?.ToString() ?? "") != kStr);
                        if ((remainingKeys == 1 || hasStoreForOtherKey) && constChecks.Count - constIdx > 1)
                        {
                            var orList = new List<PatternNode>();
                            while (constIdx < constChecks.Count)
                            {
                                orList.Add(new MatchValuePattern(new ConstantExpr(constChecks[constIdx++])));
                            }
                            keyPatterns[kStr] = new MatchOrPattern(orList);
                        }
                        else
                        {
                            keyPatterns[kStr] = new MatchValuePattern(new ConstantExpr(constChecks[constIdx++]));
                        }
                    }
                }

                foreach (var kObj in keysTuple)
                {
                    var kStr = kObj?.ToString() ?? "";
                    if (keyPatterns.ContainsKey(kStr)) continue;

                    var matchingDirectStore = storesInCase.FirstOrDefault(s => !usedStores.Contains(s) && (s == kStr || s.Contains(kStr) || kStr.Contains(s)));
                    if (matchingDirectStore != null && classChecks.Count > 0 && keysTuple.Length > 2)
                    {
                        usedStores.Add(matchingDirectStore);
                        keyPatterns[kStr] = new MatchAsPattern(null, matchingDirectStore);
                        continue;
                    }

                    if (classIdx < classChecks.Count)
                    {
                        var cls = classChecks[classIdx++];
                        var argVar = !string.IsNullOrEmpty(cls.ArgVar) ? cls.ArgVar : (storesInCase.FirstOrDefault(s => !usedStores.Contains(s)) ?? "__pycrune_ph_x");
                        usedStores.Add(argVar);
                        keyPatterns[kStr] = new MatchClassPattern(new NameExpr(cls.Name), [new MatchAsPattern(null, argVar)]);
                        continue;
                    }

                    var directStore = storesInCase.FirstOrDefault(s => !usedStores.Contains(s)) ?? kStr;
                    usedStores.Add(directStore);
                    keyPatterns[kStr] = new MatchAsPattern(null, directStore);
                }

                foreach (var kObj in keysTuple)
                {
                    var kStr = kObj?.ToString() ?? "";
                    if (keyPatterns.TryGetValue(kStr, out var pat))
                    {
                        entries.Add((new ConstantExpr(kStr), pat));
                    }
                }

                var guardEnd = -1;
                for (var g = bodyEndIdx - 1; g >= mIdx; g--)
                {
                    if (_instrs[g].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
                    {
                        var tgt = _instrs[g].TargetOffset ?? -1;
                        if (tgt >= nextCaseOffset)
                        {
                            guardEnd = g;
                            break;
                        }
                    }
                }

                ExprNode? guard = null;
                var gStart = mIdx;
                for (var s = mIdx; s < (guardEnd > 0 ? guardEnd : bodyEndIdx); s++)
                {
                    if (_instrs[s].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.DELETE_SUBSCR)
                    {
                        if (s > 0 && _instrs[s - 1].OpCode == OpCodes.COPY && (_instrs[s - 1].OpArg ?? 0) == 1)
                            continue;
                        gStart = s + 1;
                    }
                }
                while (gStart < guardEnd && _instrs[gStart].OpCode is OpCodes.POP_TOP or OpCodes.SWAP) gStart++;

                if (guardEnd > gStart)
                {
                    var gStack = new SymbolicStack();
                    DecompileRange(gStart, guardEnd, gStack);
                    if (gStack.Count > 0) guard = gStack.Pop();
                    mIdx = guardEnd + 1;
                }
                else if (guardEnd > 0)
                {
                    mIdx = guardEnd + 1;
                }
                else
                {
                    mIdx = gStart;
                }

                while (mIdx < bodyEndIdx && _instrs[mIdx].OpCode == OpCodes.POP_TOP) mIdx++;

                var bodyStartIdx = mIdx;
                while (bodyEndIdx > bodyStartIdx && _instrs[bodyEndIdx - 1].OpCode is OpCodes.NOP or OpCodes.POP_TOP) bodyEndIdx--;
                if (bodyEndIdx > bodyStartIdx && _instrs[bodyEndIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                {
                    matchExitOffset = Math.Max(matchExitOffset, _instrs[bodyEndIdx - 1].TargetOffset ?? -1);
                    bodyEndIdx--;
                }

                var body = DecompileRange(bodyStartIdx, bodyEndIdx, new SymbolicStack());
                cases.Add(new MatchCase(new MatchMappingPattern(entries, restPattern), guard, body));
                if (bodyEndIdx >= 0) lastCaseEndIdx = bodyEndIdx;

                if (nextCaseOffset >= 0 && _offsetToIndex.TryGetValue(nextCaseOffset, out var nxtIdx))
                {
                    curIdx = Math.Max(loopStartIdx + 1, nxtIdx);
                }
                else
                {
                    break;
                }
                continue;
            }

            curIdx++;
        }

        if (cases.Count == 0)
        {
            stack.Push(subject);
            return false;
        }

        matchStmt = new MatchStmt(subject, cases);
        if (matchExitOffset >= 0 && _offsetToIndex.TryGetValue(matchExitOffset, out var meIdx))
        {
            nextIdx = meIdx;
        }
        else
        {
            nextIdx = lastCaseEndIdx >= 0 ? Math.Min(lastCaseEndIdx, endIdx) : endIdx;
        }
        return true;
    }

    private PatternNode ParseClassPatternArgs(ref int pIdx, int endIdx)
    {
        var clsName = _instrs[pIdx].OpCode is OpCodes.COPY ? (_instrs[pIdx + 1].ArgRepr ?? _instrs[pIdx + 1].ArgVal?.ToString() ?? throw new PatParseException("pattern class name missing")) : (_instrs[pIdx].ArgRepr ?? _instrs[pIdx].ArgVal?.ToString() ?? throw new PatParseException("pattern class name missing"));
        var matchIdx = pIdx;
        while (matchIdx < endIdx && _instrs[matchIdx].OpCode != OpCodes.MATCH_CLASS) matchIdx++;
        if (matchIdx >= endIdx) return new MatchWildcardPattern();

        var posArgsCount = _instrs[matchIdx].OpArg ?? 0;
        var kwIdx = matchIdx - 1;
        var kwNames = (_instrs[kwIdx].ArgVal as IEnumerable<object?>)?.Select(o => o?.ToString() ?? "").ToList() ?? new List<string>();
        var totalExpectedArgs = posArgsCount + kwNames.Count;

        var jumpIdx = matchIdx + 1;
        while (jumpIdx < endIdx && _instrs[jumpIdx].OpCode is not (OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_FALSE)) jumpIdx++;
        pIdx = jumpIdx + 1;
        if (pIdx < endIdx && _instrs[pIdx].OpCode == OpCodes.UNPACK_SEQUENCE) pIdx++;

        var classArgs = new List<PatternNode>();
        for (var ai = 0; ai < totalExpectedArgs && pIdx < endIdx; ai++)
        {
            var attrName = ai < posArgsCount ? null : kwNames[ai - posArgsCount];
            PatternNode argPat;

            if (pIdx + 2 < endIdx && (_instrs[pIdx].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME || (_instrs[pIdx].OpCode == OpCodes.COPY && _instrs[pIdx + 1].OpCode is OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)) &&
                (_instrs[pIdx + 2].OpCode == OpCodes.MATCH_CLASS || (pIdx + 3 < endIdx && _instrs[pIdx + 3].OpCode == OpCodes.MATCH_CLASS)))
            {
                argPat = ParseClassPatternArgs(ref pIdx, endIdx);
            }
            else if (_instrs[pIdx].OpCode == OpCodes.LOAD_CONST)
            {
                var cVal = _instrs[pIdx].ArgVal;
                argPat = new MatchValuePattern(new ConstantExpr(cVal));
                pIdx++;
                while (pIdx < endIdx && _instrs[pIdx].OpCode is OpCodes.COMPARE_OP or OpCodes.POP_JUMP_IF_FALSE) pIdx++;
            }
            else if (_instrs[pIdx].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
            {
                var vName = _instrs[pIdx].ArgRepr ?? _instrs[pIdx].ArgVal?.ToString() ?? "__pycrune_ph_x";
                argPat = new MatchAsPattern(null, vName);
                pIdx++;
            }
            else if (_instrs[pIdx].OpCode == OpCodes.POP_TOP)
            {
                argPat = new MatchWildcardPattern();
                pIdx++;
            }
            else
            {
                argPat = new MatchWildcardPattern();
                pIdx++;
            }

            if (!string.IsNullOrEmpty(attrName))
            {
                argPat = new MatchKeywordPattern(attrName, argPat);
            }
            classArgs.Add(argPat);
        }

        while (pIdx < endIdx && _instrs[pIdx].OpCode == OpCodes.UNPACK_SEQUENCE) pIdx++;
        PatternNode classPat = new MatchClassPattern(new NameExpr(clsName), classArgs);
        if (totalExpectedArgs == 0 && pIdx < endIdx && _instrs[pIdx].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME)
        {
            var asName = _instrs[pIdx].ArgRepr ?? _instrs[pIdx].ArgVal?.ToString() ?? "__pycrune_ph_x";
            pIdx++;
            classPat = new MatchAsPattern(classPat, asName);
        }
        return classPat;
    }

    private int ResolveJumpTarget(int offset)
    {
        while (_offsetToIndex.TryGetValue(offset, out var idx) && idx < _instrs.Count)
        {
            var ins = _instrs[idx];
            if (ins.OpCode == OpCodes.JUMP_FORWARD)
            {
                offset = ins.TargetOffset ?? offset;
            }
            else
            {
                break;
            }
        }
        return offset;
    }

        private bool IsOffsetProtected(int offset)
    {
        foreach (var e in _code.ExceptionTable)
        {
            if (e.Encloses(offset)) return true;
        }
        return false;
    }

        private bool TryDecompileDeadTryFinally(int idx, out StmtNode stmt, out int nextIdx)
    {
        stmt = null!;
        nextIdx = idx;


        var p = idx;
        var sawNop = false;
        while (p < _instrs.Count &&
               _instrs[p].OpCode is OpCodes.NOP or OpCodes.EXTENDED_ARG or OpCodes.NOT_TAKEN)
        {
            if (_instrs[p].OpCode == OpCodes.NOP) sawNop = true;
            p++;
        }
        if (!sawNop) return false;
        int Take(int expected)
        {
            p = SkipTransparent(p);
            if (p >= _instrs.Count || _instrs[p].OpCode != expected) return -1;
            return p++;
        }

        var ret1 = Take(OpCodes.RETURN_CONST);
        var push = Take(OpCodes.PUSH_EXC_INFO);
        var pop = Take(OpCodes.POP_TOP);
        var popEx = Take(OpCodes.POP_EXCEPT);
        var ret2 = Take(OpCodes.RETURN_CONST);
        var copy = Take(OpCodes.COPY);
        var popEx2 = Take(OpCodes.POP_EXCEPT);
        var rer = Take(OpCodes.RERAISE);
        if (ret1 < 0 || push < 0 || pop < 0 || popEx < 0 || ret2 < 0 || copy < 0 || popEx2 < 0 || rer < 0)
        {
            return false;
        }

        if (!Equals(_instrs[ret1].ArgVal, _instrs[ret2].ArgVal)) return false;

        var pushOff = _instrs[push].Offset;
        var popOff = _instrs[pop].Offset;
        var copyOff = _instrs[copy].Offset;
        var hasCleanup = false;
        foreach (var e in _code.ExceptionTable)
        {
            if (e.Start == pushOff && e.End == popOff + 2 && e.Target == copyOff && e.LastI && e.Depth == 1)
            {
                hasCleanup = true;
                break;
            }
        }
        if (!hasCleanup) return false;

        stmt = new TryStmt(
            [new ReturnStmt(null)],
            [],
            [],
            [new ReturnStmt(new ConstantExpr(_instrs[ret1].ArgVal))]);
        nextIdx = rer + 1;
        return true;
    }

        private void AppendUnusedNameReferences(List<StmtNode> stmts)
    {
        if (stmts.Count == 0 || !AlwaysTerminates(stmts[^1])) return;

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ins in _instrs)
        {
            if (ins.ArgVal is string s) used.Add(s);
        }

        foreach (var name in _code.Names)
        {
            if (!used.Contains(name) && !string.IsNullOrEmpty(name))
            {
                stmts.Add(new ExprStmt(new NameExpr(name)));
            }
        }
    }

    private static bool AlwaysTerminates(StmtNode stmt) => stmt switch
    {
        ReturnStmt => true,
        RaiseStmt => true,
        TryStmt t when t.FinallyBody is { Count: > 0 } fin => AlwaysTerminates(fin[^1]),
        IfStmt i when i.ElseBody.Count > 0 =>
            i.ThenBody.Count > 0 && AlwaysTerminates(i.ThenBody[^1]) &&
            AlwaysTerminates(i.ElseBody[^1]),
        _ => false
    };

    private static bool HasStatementOpcodeBetween(IReadOnlyList<PythonInstruction> instrs, int startIdx, int endIdx)
    {
        for (var i = startIdx; i < endIdx && i < instrs.Count; i++)
        {
            var op = instrs[i].OpCode;
            if (op is OpCodes.POP_TOP or OpCodes.STORE_NAME or OpCodes.STORE_FAST or
                OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF or OpCodes.STORE_SUBSCR or
                OpCodes.STORE_ATTR or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or
                OpCodes.JUMP_FORWARD or OpCodes.RAISE_VARARGS or OpCodes.RERAISE or
                OpCodes.DELETE_NAME or OpCodes.DELETE_FAST or OpCodes.DELETE_GLOBAL or
                OpCodes.DELETE_SUBSCR or OpCodes.DELETE_ATTR)
            {
                return true;
            }
        }
        return false;
    }

        private int FindFinallyHandlerEndIdx(int finHIdx)
    {
        var fhStart = finHIdx;
        if (fhStart < _instrs.Count && _instrs[fhStart].OpCode == OpCodes.PUSH_EXC_INFO) fhStart++;
        var fhEnd = fhStart;
        var nesting = 0;
        while (fhEnd < _instrs.Count)
        {
            var op = _instrs[fhEnd].OpCode;
            if (op == OpCodes.PUSH_EXC_INFO) nesting++;
            else if (op == OpCodes.POP_EXCEPT) { if (nesting > 0) nesting--; }
            else if (op is OpCodes.RERAISE or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                if (nesting == 0) break;
            }
            fhEnd++;
        }
        if (fhEnd < _instrs.Count && _instrs[fhEnd].OpCode == OpCodes.RERAISE && (_instrs[fhEnd].OpArg ?? 0) == 0 &&
            fhEnd + 3 < _instrs.Count && _instrs[fhEnd + 1].OpCode == OpCodes.COPY &&
            _instrs[fhEnd + 2].OpCode == OpCodes.POP_EXCEPT && _instrs[fhEnd + 3].OpCode == OpCodes.RERAISE)
        {
            return fhEnd + 4;
        }
        return fhEnd + 1;
    }

    private int IndexInVarNames(string name)
    {
        for (var i = 0; i < _code.VarNames.Count; i++)
        {
            if (_code.VarNames[i] == name) return i;
        }
        return -1;
    }

    private static IEnumerable<string> SimpleAssignedNames(IReadOnlyList<StmtNode> stmts)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case AssignStmt a:
                    foreach (var t in a.Targets)
                    {
                        if (t is NameExpr n) yield return n.Name;
                    }
                    break;
                case AugAssignStmt aa when aa.Target is NameExpr an:
                    yield return an.Name;
                    break;
                case ForStmt f when f.Target is NameExpr fn:
                    yield return fn.Name;
                    break;
                case IfStmt i:
                    foreach (var n in SimpleAssignedNames(i.ThenBody)) yield return n;
                    foreach (var n in SimpleAssignedNames(i.ElseBody)) yield return n;
                    break;
                case WhileStmt w:
                    foreach (var n in SimpleAssignedNames(w.Body)) yield return n;
                    foreach (var n in SimpleAssignedNames(w.ElseBody)) yield return n;
                    break;
                case TryStmt t:
                    foreach (var n in SimpleAssignedNames(t.Body)) yield return n;
                    foreach (var h in t.Handlers)
                    {
                        foreach (var n in SimpleAssignedNames(h.Body)) yield return n;
                    }
                    foreach (var n in SimpleAssignedNames(t.ElseBody)) yield return n;
                    if (t.FinallyBody != null)
                    {
                        foreach (var n in SimpleAssignedNames(t.FinallyBody)) yield return n;
                    }
                    break;
                case WithStmt wi:
                    foreach (var n in SimpleAssignedNames(wi.Body)) yield return n;
                    break;
            }
        }
    }

    private static bool HasRealStatementsBetween(IReadOnlyList<PythonInstruction> instrs, int startIdx, int endIdx)    {
        for (var i = startIdx; i < endIdx && i < instrs.Count; i++)
        {
            var op = instrs[i].OpCode;
            if (op is not (OpCodes.NOP or OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT or OpCodes.EXTENDED_ARG))
            {
                return true;
            }
        }
        return false;
    }

    private bool TryGetBranchInfo(PythonInstruction ins, SymbolicStack stack, out ExprNode cond, out bool jumpIfTrue, out int target)
    {
        cond = null!;
        jumpIfTrue = false;
        target = ResolveJumpTarget(ins.TargetOffset ?? 0);

        if (stack.Count == 0) return false;

        switch (ins.OpCode)
        {
            case OpCodes.POP_JUMP_IF_TRUE:
                cond = stack.Pop();
                jumpIfTrue = true;
                return true;
            case OpCodes.POP_JUMP_IF_FALSE:
                cond = stack.Pop();
                jumpIfTrue = false;
                return true;
            case OpCodes.POP_JUMP_IF_NONE:
                cond = new CompareExpr(stack.Pop(), [("is not", new NameExpr("None"))]);
                jumpIfTrue = false;
                return true;
            case OpCodes.POP_JUMP_IF_NOT_NONE:
                cond = new CompareExpr(stack.Pop(), [("is", new NameExpr("None"))]);
                jumpIfTrue = false;
                return true;
            default:
                return false;
        }
    }

    private bool Is311()
    {
        var version = _code.PythonVersion;
        return !string.IsNullOrEmpty(version) && version.StartsWith("3.11", StringComparison.Ordinal);
    }

    private bool IsAtLeast311()
    {
        var version = _code.PythonVersion;
        if (string.IsNullOrEmpty(version)) return false;
        var parts = version.Split('.');
        return parts.Length >= 2 &&
               int.TryParse(parts[0], out var major) &&
               int.TryParse(parts[1], out var minor) &&
               (major > 3 || (major == 3 && minor >= 11));
    }

    private bool Is310Or311()
    {
        var version = _code.PythonVersion;
        return version.StartsWith("3.10", StringComparison.Ordinal) ||
               version.StartsWith("3.11", StringComparison.Ordinal);
    }

    private HashSet<int>? _usedConstIndices;
    private Dictionary<int, int>? _firstConstLoadPositions;
    private readonly HashSet<int> _lateFoldConstantsUsed = [];
    private bool _foldedOperandUsed;

    private HashSet<int> UsedConstIndices()
    {
        if (_usedConstIndices != null) return _usedConstIndices;
        var used = new HashSet<int>();
        foreach (var ins in _instrs)
        {


            if (ins.OpCode == OpCodes.LOAD_CONST && ins.OpName == "LOAD_CONST" && ins.OpArg is int idx)
            {
                used.Add(idx);
            }
            else if (ins.OpCode == OpCodes.KW_NAMES && ins.OpArg is int kwIdx)
            {
                used.Add(kwIdx);
            }
            else if (ins.OpCode == OpCodes.RETURN_CONST && ins.OpName == "RETURN_CONST" && ins.OpArg is int retIdx)
            {
                used.Add(retIdx);
            }
        }
        _usedConstIndices = used;
        return used;
    }

    private Dictionary<int, int> FirstConstLoadPositions()
    {
        if (_firstConstLoadPositions != null) return _firstConstLoadPositions;
        var first = new Dictionary<int, int>();
        for (var i = 0; i < _instrs.Count; i++)
        {
            var ins = _instrs[i];
            if (ins.OpCode == OpCodes.LOAD_CONST && ins.OpName == "LOAD_CONST" && ins.OpArg is int idx)
            {
                first.TryAdd(idx, i);
            }
        }
        _firstConstLoadPositions = first;
        return first;
    }

        private bool IsOutOfOrderConstant(int valueIndex, int loadPosition)
    {
        if (valueIndex < 0 || loadPosition < 0) return false;
        var first = FirstConstLoadPositions();
        for (var u = 0; u < valueIndex; u++)
        {
            if (IsCompilerGeneratedConstant(_code.Constants[u])) continue;
            if (first.TryGetValue(u, out var later) && later > loadPosition) return true;
        }
        return false;
    }

    private static bool IsCompilerGeneratedConstant(object? value)
    {
        if (value is PycRune.Model.SliceValue) return true;
        if (value is string s && s.StartsWith("<code:", StringComparison.Ordinal)) return true;
        if (value is System.Collections.IEnumerable sequence and not string)
        {
            foreach (var item in sequence)
            {
                if (item is PycRune.Model.SliceValue) return true;
            }
        }
        return false;
    }

        private bool IsConstantInsertedLate(int constantIndex)
    {
        var first = FirstConstLoadPositions();
        if (!first.TryGetValue(constantIndex, out var position)) return false;
        for (var higher = constantIndex + 1; higher < _code.Constants.Count; higher++)
        {
            if (first.TryGetValue(higher, out var higherPosition) && higherPosition < position) return true;
        }
        return false;
    }

        private bool TryReconstructFoldedConstant(PythonInstruction ins, out ExprNode expr)
    {
        expr = null!;
        if (!IsAtLeast314() || ins.ArgVal == null) return false;

        var consts = _code.Constants;
        if (consts.Count == 0) return false;

        var loadPosition = _offsetToIndex.TryGetValue(ins.Offset, out var pos) ? pos : -1;
        var valueIndex = ins.OpName == "LOAD_CONST" && ins.OpArg is int idx ? idx : -1;
        var outOfOrder = IsOutOfOrderConstant(valueIndex, loadPosition);

        if (outOfOrder)
        {


            if (!_foldedOperandUsed && !UsedConstIndices().Contains(0) && consts[0] is not null &&
                TryBuildFoldWithOperand(consts[0], ins.ArgVal, out var operandFold) &&
                IsDelayedFoldExpr(operandFold))
            {
                expr = operandFold;
                _foldedOperandUsed = true;
                return true;
            }
            return TryBuildDelayedFold(ins.ArgVal, out expr);
        }

        if (!_foldedOperandUsed && !UsedConstIndices().Contains(0) && consts[0] is not null &&
            TryBuildFoldWithOperand(consts[0], ins.ArgVal, out expr))
        {
            _foldedOperandUsed = true;
            return true;
        }

        if (IsIntegerValue(ins.ArgVal) && ToBigInteger(ins.ArgVal) == 2)
        {
            for (var i = 0; i < consts.Count; i++)
            {
                if (consts[i] is not bool boolConstant || !boolConstant) continue;
                if (_lateFoldConstantsUsed.Contains(i)) continue;
                if (!IsConstantInsertedLate(i)) continue;
                expr = new BinaryExpr(new ConstantExpr(true), "+", new ConstantExpr(true));
                _lateFoldConstantsUsed.Add(i);
                return true;
            }
        }
        return false;
    }

        private static bool TryBuildDelayedFold(object value, out ExprNode expr)
    {
        expr = null!;
        if (IsIntegerValue(value))
        {
            var v = ToBigInteger(value);


            if (v > -1_000_000_000 && v < 1_000_000_000) return false;
            if (TryLog2Big(v, out var shift))
            {
                expr = new BinaryExpr(new ConstantExpr(1), "<<", new ConstantExpr(shift));
                return true;
            }
            for (var b = 10; b >= 2; b--)
            {
                if (TryPerfectPower(v, b, out var exponent))
                {
                    expr = new BinaryExpr(new ConstantExpr(b), "**", new ConstantExpr(exponent));
                    return true;
                }
            }
            expr = new BinaryExpr(new ConstantExpr(1), "+", IntegerConstant(v - 1));
            return true;
        }
        if (IsFloatValue(value))
        {
            return TryBuildDelayedFloatFold(Convert.ToDouble(value), out expr);
        }
        return false;
    }

    private static bool TryBuildDelayedFloatFold(double value, out ExprNode expr)
    {
        expr = null!;
        if (!double.IsFinite(value)) return false;
        ReadOnlySpan<double> factors = [3.0, 2.0, 4.0, 0.5, 5.0, 7.0, 10.0, 0.25, 1.5, 2.5];
        foreach (var divisor in factors)
        {
            if (divisor == 0) continue;
            var dividend = value * divisor;
            if (double.IsFinite(dividend) && dividend / divisor == value)
            {
                expr = new BinaryExpr(new ConstantExpr(dividend), "/", new ConstantExpr(divisor));
                return true;
            }
        }
        foreach (var factor in factors)
        {
            if (!double.IsFinite(factor) || factor == 0 || factor == 1.0) continue;
            var other = value / factor;
            if (double.IsFinite(other) && other != 1.0 && factor * other == value)
            {
                expr = new BinaryExpr(new ConstantExpr(factor), "*", new ConstantExpr(other));
                return true;
            }
        }
        foreach (var addend in factors)
        {
            var other = value - addend;
            if (double.IsFinite(other) && other != 0.0 && addend + other == value)
            {
                expr = new BinaryExpr(new ConstantExpr(addend), "+", new ConstantExpr(other));
                return true;
            }
        }
        return false;
    }

        private static bool IsDelayedFoldExpr(ExprNode expr) => expr switch
    {
        BinaryExpr { Op: "<<" or "**" } => true,
        BinaryExpr { Op: "+" or "-" } b => !IsConstantZero(b.Left) && !IsConstantZero(b.Right),
        BinaryExpr { Op: "*" } b => !IsConstantZero(b.Left) && !IsConstantZero(b.Right) &&
                                    !IsConstantOne(b.Left) && !IsConstantOne(b.Right),
        BinaryExpr { Op: "/" } b => !IsConstantZero(b.Right) && !IsConstantOne(b.Right),
        _ => false
    };

    private static bool IsConstantZero(ExprNode expr) =>
        expr is ConstantExpr constant && IsNumericZero(constant.Value);

    private static bool IsConstantOne(ExprNode expr) =>
        expr is ConstantExpr constant && IsNumericOne(constant.Value);

    private static bool IsNumericZero(object? value) => value switch
    {
        long l => l == 0,
        int i => i == 0,
        uint ui => ui == 0,
        ulong ul => ul == 0,
        short s => s == 0,
        ushort us => us == 0,
        byte b => b == 0,
        sbyte sb => sb == 0,
        System.Numerics.BigInteger bi => bi.IsZero,
        double d => d == 0.0,
        float f => f == 0.0f,
        System.Numerics.Complex c => c == System.Numerics.Complex.Zero,
        _ => false
    };

    private static bool IsNumericOne(object? value) => value switch
    {
        long l => l == 1,
        int i => i == 1,
        uint ui => ui == 1,
        ulong ul => ul == 1,
        short s => s == 1,
        ushort us => us == 1,
        byte b => b == 1,
        sbyte sb => sb == 1,
        System.Numerics.BigInteger bi => bi.IsOne,
        double d => d == 1.0,
        float f => f == 1.0f,
        System.Numerics.Complex c => c == System.Numerics.Complex.One,
        _ => false
    };

    private static bool TryBuildFoldWithOperand(object operand, object value, out ExprNode expr)
    {
        expr = null!;
        var operandExpr = new ConstantExpr(operand);

        if (operand is bool b)
        {
            if (b)
            {
                if (IsIntegerValue(value))
                {
                    var v = ToBigInteger(value);
                    if (v == 2)
                    {
                        expr = new BinaryExpr(new ConstantExpr(true), "+", new ConstantExpr(true));
                        return true;
                    }
                    expr = new BinaryExpr(operandExpr, "*", IntegerConstant(v));
                    return true;
                }
                if (IsFloatValue(value) || IsComplexValue(value))
                {
                    expr = new BinaryExpr(operandExpr, "*", new ConstantExpr(value));
                    return true;
                }
            }
            else if (IsIntegerValue(value) && ToBigInteger(value) == 0)
            {
                expr = new BinaryExpr(operandExpr, "*", new ConstantExpr(0));
                return true;
            }
            return false;
        }

        if (IsIntegerValue(operand))
        {
            var a = ToBigInteger(operand);
            if (IsIntegerValue(value))
            {
                var v = ToBigInteger(value);
                if (a == 1 && TryLog2Big(v, out var shift))
                {
                    expr = new BinaryExpr(new ConstantExpr(1), "<<", new ConstantExpr(shift));
                    return true;
                }
                if (a >= 2 && TryPerfectPower(v, a, out var exponent))
                {
                    expr = new BinaryExpr(operandExpr, "**", new ConstantExpr(exponent));
                    return true;
                }
                if (a != 0 && v % a == 0)
                {
                    expr = new BinaryExpr(operandExpr, "*", IntegerConstant(v / a));
                    return true;
                }
                expr = new BinaryExpr(operandExpr, "+", IntegerConstant(v - a));
                return true;
            }
            if (IsFloatValue(value))
            {
                var av = (double)a;
                if (TryBuildFloatFold(operandExpr, av, Convert.ToDouble(value), out expr)) return true;
            }
            if (IsComplexValue(value))
            {
                var av = new System.Numerics.Complex((double)a, 0);
                if (TryBuildComplexFold(operandExpr, av, (System.Numerics.Complex)value, out expr)) return true;
            }
            return false;
        }

        if (IsFloatValue(operand) && IsFloatValue(value))
        {
            if (TryBuildFloatFold(operandExpr, Convert.ToDouble(operand), Convert.ToDouble(value), out expr)) return true;
            return false;
        }

        if (IsComplexValue(operand) && IsComplexValue(value))
        {
            if (TryBuildComplexFold(operandExpr, (System.Numerics.Complex)operand, (System.Numerics.Complex)value, out expr)) return true;
            return false;
        }

        return false;
    }

    private static bool TryBuildFloatFold(ExprNode operandExpr, double a, double v, out ExprNode expr)
    {
        expr = null!;
        if (a != 0 && double.IsFinite(a))
        {
            var scaled = v / a;
            if (a * scaled == v)
            {
                expr = new BinaryExpr(operandExpr, "*", new ConstantExpr(scaled));
                return true;
            }
            var one = a / a;
            if (one == 1.0 && 1.0 * v == v)
            {
                expr = new BinaryExpr(
                    new BinaryExpr(operandExpr, "/", new ConstantExpr(a)),
                    "*", new ConstantExpr(v));
                return true;
            }
        }
        var delta = v - a;
        if (a + delta == v)
        {
            expr = new BinaryExpr(operandExpr, "+", new ConstantExpr(delta));
            return true;
        }
        var back = a - v;
        if (a - back == v)
        {
            expr = new BinaryExpr(operandExpr, "-", new ConstantExpr(back));
            return true;
        }
        return false;
    }

    private static bool TryBuildComplexFold(
        ExprNode operandExpr, System.Numerics.Complex a, System.Numerics.Complex v, out ExprNode expr)
    {
        expr = null!;
        if (a != System.Numerics.Complex.Zero &&
            double.IsFinite(a.Real) && double.IsFinite(a.Imaginary))
        {
            var scaled = v / a;
            if (a * scaled == v)
            {
                expr = new BinaryExpr(operandExpr, "*", new ConstantExpr(scaled));
                return true;
            }
            var one = a / a;
            if (one == System.Numerics.Complex.One && System.Numerics.Complex.One * v == v)
            {
                expr = new BinaryExpr(
                    new BinaryExpr(operandExpr, "/", new ConstantExpr(a)),
                    "*", new ConstantExpr(v));
                return true;
            }
        }
        var delta = v - a;
        if (a + delta == v)
        {
            expr = new BinaryExpr(operandExpr, "+", new ConstantExpr(delta));
            return true;
        }
        var back = a - v;
        if (a - back == v)
        {
            expr = new BinaryExpr(operandExpr, "-", new ConstantExpr(back));
            return true;
        }
        return false;
    }

    private static bool IsIntegerValue(object? value) =>
        value is long or int or uint or ulong or short or ushort or byte or sbyte or
            System.Numerics.BigInteger;

    private static bool IsFloatValue(object? value) => value is double or float;

    private static bool IsComplexValue(object? value) => value is System.Numerics.Complex;

    private static System.Numerics.BigInteger ToBigInteger(object value) => value switch
    {
        System.Numerics.BigInteger bi => bi,
        long l => l,
        int i => i,
        uint ui => ui,
        ulong ul => ul,
        short sh => sh,
        ushort ush => ush,
        byte by => by,
        sbyte sb => sb,
        _ => System.Numerics.BigInteger.Zero
    };

    private static ConstantExpr IntegerConstant(System.Numerics.BigInteger value) =>
        value >= long.MinValue && value <= long.MaxValue
            ? new ConstantExpr((long)value)
            : new ConstantExpr(value);

    private static bool TryPerfectPower(
        System.Numerics.BigInteger value, System.Numerics.BigInteger baseValue, out int exponent)
    {
        exponent = 0;
        if (value <= 1 || baseValue <= 1) return false;
        var power = baseValue;
        for (var e = 2; e <= 4096; e++)
        {
            power *= baseValue;
            if (power == value)
            {
                exponent = e;
                return true;
            }
            if (power > value) return false;
        }
        return false;
    }

    private static bool TryLog2Big(System.Numerics.BigInteger value, out int shift)
    {
        shift = 0;
        if (value <= 0) return false;
        while (value > 1)
        {
            if (!value.IsEven) return false;
            value >>= 1;
            shift++;
        }
        return value == 1;
    }

        private void PreserveUnreachableElse(List<StmtNode> statements)
    {
        var usedConstants = UsedConstIndices();
        var unusedConstants = _code.Constants
            .Select((value, index) => (value, index))
            .Where(x => !usedConstants.Contains(x.index) && IsPreservableDeadConstant(x.value))
            .Select(x => x.value!)
            .ToList();

        var usedNames = UsedNameSet();
        var unusedNames = _code.Names
            .Where(name => !usedNames.Contains(name) && IsValidPythonIdentifier(name))
            .ToList();

        if (unusedConstants.Count == 0 && unusedNames.Count == 0) return;

        var callee = unusedNames.FirstOrDefault() ?? FindDeadElseCallee();
        if (unusedConstants.Count > 0 && callee == null) return;

        var constantCursor = 0;
        Rewrite(statements);
        return;

        void Rewrite(List<StmtNode> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var stmt = list[i];
                if (stmt is TryStmt tryStmt)
                {
                    var handlers = tryStmt.Handlers;
                    for (var h = 0; h < handlers.Count; h++)
                    {
                        if (handlers[h].Body is List<StmtNode> bodyList) Rewrite(bodyList);
                    }
                    if (tryStmt.ElseBody is List<StmtNode> elseList) Rewrite(elseList);
                    if (tryStmt.FinallyBody is List<StmtNode> finallyList) Rewrite(finallyList);
                    if (tryStmt.ElseBody.Count == 0 && tryStmt.FinallyBody != null &&
                        handlers.Count > 0 && tryStmt.Body.Count > 0 &&
                        tryStmt.Body[^1] is RaiseStmt or ReturnStmt or BreakStmt or ContinueStmt)
                    {
                        var isStar = handlers.Any(h => h.IsStar);
                        if (!isStar && !Is310Or311()) continue;
                        var constants = unusedConstants;
                        var names = isStar ? unusedNames : [];
                        if (constants.Count > 0 && callee == null) continue;
                        var deadElse = new List<StmtNode>();
                        foreach (var name in names)
                        {
                            deadElse.Add(new ExprStmt(new NameExpr(name)));
                        }
                        if (constantCursor < constants.Count &&
                            ContainsCallWithConstant(tryStmt.FinallyBody))
                        {
                            var value = constants[constantCursor++];
                            deadElse.Add(new ExprStmt(new CallExpr(
                                new NameExpr(callee!), [new ConstantExpr(value)], [])));
                        }
                        if (deadElse.Count > 0) list[i] = tryStmt with { ElseBody = deadElse };
                    }
                    if (tryStmt.Body is List<StmtNode> tryList) Rewrite(tryList);
                }
                else if (stmt is WithStmt withStmt && withStmt.Body is List<StmtNode> withBody)
                {
                    Rewrite(withBody);
                }
                else if (stmt is IfStmt ifStmt)
                {
                    Rewrite(ifStmt.ThenBody.ToList());
                    Rewrite(ifStmt.ElseBody.ToList());
                }
                else if (stmt is ForStmt forStmt)
                {
                    Rewrite(forStmt.Body.ToList());
                    Rewrite(forStmt.ElseBody.ToList());
                }
                else if (stmt is WhileStmt whileStmt)
                {
                    Rewrite(whileStmt.Body.ToList());
                    Rewrite(whileStmt.ElseBody.ToList());
                }
            }
        }
    }

    private HashSet<string> UsedNameSet()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ins in _instrs)
        {
            if (ins.ArgVal is string value) AddName(used, value);
            if (ins.ArgRepr is string repr) AddName(used, repr);
        }
        return used;

        static void AddName(HashSet<string> set, string value)
        {
            if (value.StartsWith("NULL|self + ", StringComparison.Ordinal)) value = value[12..];
            else if (value.StartsWith("NULL + ", StringComparison.Ordinal)) value = value[7..];
            if (value.EndsWith(" + NULL|self", StringComparison.Ordinal)) value = value[..^12];
            else if (value.EndsWith(" + NULL", StringComparison.Ordinal)) value = value[..^7];
            if (!string.IsNullOrEmpty(value)) set.Add(value);
        }
    }

    private string? FindDeadElseCallee()
    {
        foreach (var name in _code.Names)
        {
            if (IsValidPythonIdentifier(name)) return name;
        }
        foreach (var name in _code.VarNames)
        {
            if (IsValidPythonIdentifier(name)) return name;
        }
        return null;
    }

    private static bool IsPreservableDeadConstant(object? value) => value switch
    {
        null => false,
        string s => !s.StartsWith("<code:", StringComparison.Ordinal),
        bool => true,
        long or int or uint or ulong or short or ushort or byte or sbyte => true,
        System.Numerics.BigInteger => true,
        double or float => true,
        System.Numerics.Complex => true,
        _ => false
    };

    private static bool ContainsCallWithConstant(IReadOnlyList<StmtNode> statements)
    {
        foreach (var stmt in statements)
        {
            if (StatementContainsCallWithConstant(stmt)) return true;
        }
        return false;
    }

    private static bool StatementContainsCallWithConstant(StmtNode stmt) => stmt switch
    {
        ExprStmt e => ExprContainsCallWithConstant(e.Expression),
        AssignStmt a => ExprContainsCallWithConstant(a.Value) ||
                        a.Targets.Any(ExprContainsCallWithConstant),
        AugAssignStmt a => ExprContainsCallWithConstant(a.Value) ||
                           ExprContainsCallWithConstant(a.Target),
        ReturnStmt r => r.Value != null && ExprContainsCallWithConstant(r.Value),
        IfStmt i => ContainsCallWithConstant(i.ThenBody) || ContainsCallWithConstant(i.ElseBody),
        ForStmt f => ContainsCallWithConstant(f.Body) || ContainsCallWithConstant(f.ElseBody),
        WhileStmt w => ContainsCallWithConstant(w.Body) || ContainsCallWithConstant(w.ElseBody),
        WithStmt w => w.Items.Any(item => ExprContainsCallWithConstant(item.ContextExpr)) ||
                      ContainsCallWithConstant(w.Body),
        TryStmt t => ContainsCallWithConstant(t.Body) ||
                     t.Handlers.Any(h => ContainsCallWithConstant(h.Body)) ||
                     ContainsCallWithConstant(t.ElseBody) ||
                     (t.FinallyBody != null && ContainsCallWithConstant(t.FinallyBody)),
        _ => false
    };

    private static bool ExprContainsCallWithConstant(ExprNode expr) => expr switch
    {
        CallExpr c => c.Arguments.Any(arg => arg is ConstantExpr) ||
                      c.KeywordArguments.Any(kw => kw.Value is ConstantExpr) ||
                      ExprContainsCallWithConstant(c.Function) ||
                      c.Arguments.Any(ExprContainsCallWithConstant),
        BinaryExpr b => ExprContainsCallWithConstant(b.Left) || ExprContainsCallWithConstant(b.Right),
        UnaryExpr u => ExprContainsCallWithConstant(u.Operand),
        AttributeExpr a => ExprContainsCallWithConstant(a.Target),
        SubscriptExpr s => ExprContainsCallWithConstant(s.Target) ||
                           ExprContainsCallWithConstant(s.Index),
        TupleExpr t => t.Elements.Any(ExprContainsCallWithConstant),
        ListExpr l => l.Elements.Any(ExprContainsCallWithConstant),
        DictExpr d => d.Entries.Any(e => ExprContainsCallWithConstant(e.Value) ||
                                         (e.Key != null && ExprContainsCallWithConstant(e.Key))),
        _ => false
    };

    private static bool IsValidPythonIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith("__pycrune_ph_", StringComparison.Ordinal)) return false;
        var first = name[0];
        if (!(char.IsLetter(first) || first == '_')) return false;
        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return !PythonKeywords.Contains(name);
    }

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break",
        "class", "continue", "def", "del", "elif", "else", "except", "finally",
        "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
        "not", "or", "pass", "raise", "return", "try", "while", "with", "yield"
    };

        private static List<StmtNode> RemoveInlinedFinallyCopies(
        IReadOnlyList<StmtNode> body, IReadOnlyList<StmtNode> finallyBody)
    {
        if (finallyBody.Count == 0) return body.ToList();
        var finallyText = PythonEmitter.Emit(finallyBody);
        return ProcessBranch(body, finallyBody, finallyText);
    }

    private static List<StmtNode> ProcessBranch(
        IReadOnlyList<StmtNode> branch,
        IReadOnlyList<StmtNode> finallyBody,
        string finallyText)
    {
        var result = branch.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            result[i] = ProcessStatement(result[i], finallyBody, finallyText);
        }

        for (var a = 0; a < result.Count; a++)
        {
            if (result[a] is not (ReturnStmt or RaiseStmt or BreakStmt or ContinueStmt)) continue;
            var count = finallyBody.Count;
            if (a < count) continue;
            var start = a - count;
            var candidate = result.GetRange(start, count);
            if (PythonEmitter.Emit(candidate) != finallyText) continue;
            result.RemoveRange(start, count);
            a -= count;
        }
        return result;
    }

    private static StmtNode ProcessStatement(
        StmtNode statement,
        IReadOnlyList<StmtNode> finallyBody,
        string finallyText) => statement switch
    {
        IfStmt s => s with
        {
            ThenBody = ProcessBranch(s.ThenBody, finallyBody, finallyText),
            ElseBody = ProcessBranch(s.ElseBody, finallyBody, finallyText)
        },
        ForStmt s => s with
        {
            Body = ProcessBranch(s.Body, finallyBody, finallyText),
            ElseBody = ProcessBranch(s.ElseBody, finallyBody, finallyText)
        },
        WhileStmt s => s with
        {
            Body = ProcessBranch(s.Body, finallyBody, finallyText),
            ElseBody = ProcessBranch(s.ElseBody, finallyBody, finallyText)
        },
        WithStmt s => s with
        {
            Body = ProcessBranch(s.Body, finallyBody, finallyText)
        },
        TryStmt s => s with
        {
            Body = ProcessBranch(s.Body, finallyBody, finallyText),
            Handlers = s.Handlers
                .Select(h => h with { Body = ProcessBranch(h.Body, finallyBody, finallyText) })
                .ToList(),
            ElseBody = ProcessBranch(s.ElseBody, finallyBody, finallyText)
        },
        _ => statement
    };

    private bool IsAtLeast314()
    {
        var version = _code.PythonVersion;
        if (string.IsNullOrEmpty(version)) return false;
        var parts = version.Split('.');
        return parts.Length >= 2 &&
               int.TryParse(parts[0], out var major) &&
               int.TryParse(parts[1], out var minor) &&
               (major > 3 || (major == 3 && minor >= 14));
    }

        private bool IsFollowedOnlyByHandlerCode(PythonInstruction ins)
    {
        var nextHandlerOffset = _code.ExceptionTable
            .Select(entry => entry.Target)
            .Where(target => target > ins.Offset)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        if (!_offsetToIndex.TryGetValue(ins.Offset, out var index)) return false;
        for (var i = index + 1; i < _instrs.Count; i++)
        {
            if (_instrs[i].Offset >= nextHandlerOffset) break;
            if (_instrs[i].OpCode is OpCodes.NOP or OpCodes.EXTENDED_ARG or OpCodes.NOT_TAKEN) continue;
            return false;
        }
        return true;
    }

    private bool IsTargetedByConditionalJump(int offset)
    {
        foreach (var ins in _instrs)
        {
            if (ins.TargetOffset != offset) continue;
            if (ins.OpCode is OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_FALSE or
                OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
            {
                return true;
            }
        }
        return false;
    }

    private bool IsPre312()
    {
        var version = _code.PythonVersion;
        if (string.IsNullOrEmpty(version)) return false;
        var parts = version.Split('.');
        return parts.Length >= 2 &&
               int.TryParse(parts[0], out var major) &&
               int.TryParse(parts[1], out var minor) &&
               (major < 3 || (major == 3 && minor < 12));
    }

    private bool TryDecompileShortCircuitBoolOp(ref int idx, int endIdx, SymbolicStack stack)
    {
        if (idx + 2 >= _instrs.Count) return false;

        var ins0 = _instrs[idx];


        if (IsPre312() && ins0.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE && stack.Count > 0)
        {
            var bareTargetOffset = ResolveJumpTarget(ins0.TargetOffset ?? 0);
            if (_offsetToIndex.TryGetValue(bareTargetOffset, out var bareTargetIdx) && bareTargetIdx > idx + 1)
            {
                var nestedCopy = -1;
                for (var q = idx + 1; q + 1 < bareTargetIdx; q++)
                {
                    if (_instrs[q].OpCode != OpCodes.COPY || (_instrs[q].OpArg ?? 0) != 1) continue;
                    var qj = q + 1;
                    while (qj < bareTargetIdx && _instrs[qj].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) qj++;
                    if (qj < bareTargetIdx &&
                        _instrs[qj].OpCode is OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_FALSE)
                    {
                        nestedCopy = q;
                        break;
                    }
                }

                if (nestedCopy > idx + 1)
                {
                    var bareLeft = stack.Pop();
                    var bareRightStack = new SymbolicStack();
                    DecompileRange(idx + 1, nestedCopy, bareRightStack);
                    if (bareRightStack.Count > 0)
                    {
                        var bareRight = bareRightStack.Pop();
                        var bareOp = ins0.OpCode == OpCodes.POP_JUMP_IF_FALSE ? "and" : "or";
                        stack.Push(new BoolOpExpr(bareOp, [bareLeft, bareRight]));
                        idx = nestedCopy - 1;
                        return true;
                    }
                }
            }
        }

        var jumpPos = idx + 1;
        while (jumpPos < _instrs.Count && _instrs[jumpPos].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) jumpPos++;
        if (jumpPos + 1 >= _instrs.Count) return false;
        var popPos = jumpPos + 1;
        while (popPos < _instrs.Count && _instrs[popPos].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) popPos++;
        if (popPos >= _instrs.Count) return false;

        var ins1 = _instrs[jumpPos];
        var ins2 = _instrs[popPos];

        if (ins0.OpCode == OpCodes.COPY && (ins0.OpArg ?? 0) == 1 &&
            ins1.OpCode is OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_FALSE &&
            ins2.OpCode == OpCodes.POP_TOP)
        {
            if (stack.Count == 0) return false;

            var op = ins1.OpCode == OpCodes.POP_JUMP_IF_TRUE ? "or" : "and";
            var left = stack.Pop();
            var targetOffset = ResolveJumpTarget(ins1.TargetOffset ?? 0);
            if (!_offsetToIndex.TryGetValue(targetOffset, out var targetIdx)) return false;
            var rightEnd = targetIdx;


            for (var q = popPos + 1; q + 1 < targetIdx; q++)
            {
                if (_instrs[q].OpCode == OpCodes.COPY && (_instrs[q].OpArg ?? 0) == 1)
                {
                    var qj = q + 1;
                    while (qj < targetIdx && _instrs[qj].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) qj++;
                    if (qj < targetIdx &&
                        _instrs[qj].OpCode is OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_FALSE &&
                        (_instrs[qj].TargetOffset ?? 0) > (ins1.TargetOffset ?? 0))
                    {
                        rightEnd = q;
                        break;
                    }
                }
            }
            if (targetIdx > endIdx)
            {


                if (endIdx >= _instrs.Count ||
                    _instrs[endIdx].OpCode != OpCodes.JUMP_FORWARD ||
                    ResolveJumpTarget(_instrs[endIdx].TargetOffset ?? 0) != targetOffset)
                {
                    return false;
                }
                rightEnd = endIdx;
            }

            var rightStack = new SymbolicStack();
            var rightStmts = DecompileRange(popPos + 1, rightEnd, rightStack);
            if (rightStack.Count > 0)
            {
                var right = rightStack.Pop();
                var values = new List<ExprNode>();
                if (left is BoolOpExpr bLeft && bLeft.Op == op)
                {
                    values.AddRange(bLeft.Values);
                }
                else
                {
                    values.Add(left);
                }

                if (right is BoolOpExpr bRight && bRight.Op == op)
                {
                    values.AddRange(bRight.Values);
                }
                else
                {
                    values.Add(right);
                }

                stack.Push(new BoolOpExpr(op, values));
                idx = rightEnd - 1;
                return true;
            }
        }

        return false;
    }

    private bool TryReduceBooleanLogic(ref int idx, int endIdx, SymbolicStack stack)
    {
        var ins = _instrs[idx];
        if (!TryGetBranchInfo(ins, stack, out var cond1, out var jumpIfTrue, out var target))
        {
            return false;
        }

        if (jumpIfTrue)
        {
            if (_offsetToIndex.TryGetValue(target, out var orTargetIdx) && orTargetIdx > idx + 1 && orTargetIdx <= endIdx)
            {
                var prevIns = _instrs[orTargetIdx - 1];
                if (prevIns.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
                {
                    try
                    {
                        var innerStack = stack.Clone();
                        DecompileRange(idx + 1, orTargetIdx - 1, innerStack);
                        if (innerStack.Count > stack.Count)
                        {
                            var cond2 = innerStack.Pop();
                            stack.Push(new BoolOpExpr("or", [cond1, cond2]));
                            idx = orTargetIdx - 1;
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is not FailFastException)
                    {
                    }
                }
            }
        }
        else
        {
            if (_offsetToIndex.TryGetValue(target, out var falseTargetIdx) && falseTargetIdx <= endIdx)
            {
                var nextJumpIdx = idx + 1;
                while (nextJumpIdx < falseTargetIdx && !OpCodes.IsJump(_instrs[nextJumpIdx].OpCode))
                {
                    if (OpCodes.IsTerminator(_instrs[nextJumpIdx].OpCode)) break;
                    nextJumpIdx++;
                }

                if (nextJumpIdx < falseTargetIdx && _instrs[nextJumpIdx].TargetOffset == target)
                {
                    try
                    {
                        var innerStack = stack.Clone();
                        DecompileRange(idx + 1, nextJumpIdx, innerStack);
                        if (innerStack.Count > stack.Count)
                        {
                            var cond2 = innerStack.Pop();
                            stack.Push(new BoolOpExpr("and", [cond1, cond2]));
                            idx = nextJumpIdx;
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is not FailFastException)
                    {
                    }
                }
            }
        }

        stack.Push(cond1);
        return false;
    }

        private bool TryMatchBreakPath(int startIdx, int endIdx, out int markerIdx, out int exitStart, out int exitLen)
    {
        markerIdx = -1;
        exitStart = -1;
        exitLen = 0;
        if (_activeLoopHeaders.Count == 0) return false;
        if (startIdx < 0 || endIdx > _instrs.Count || startIdx >= endIdx) return false;

        var term = -1;
        for (var i = endIdx - 1; i >= startIdx; i--)
        {
            if (_instrs[i].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RAISE_VARARGS)
            {
                term = i;
                break;
            }
            if (_instrs[i].OpCode != OpCodes.NOP) break;
        }
        if (term < 0) return false;


        var m = term;
        while (m - 1 >= startIdx && _instrs[m - 1].OpCode is
               OpCodes.LOAD_FAST or OpCodes.LOAD_CONST or OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME or
               OpCodes.LOAD_DEREF or OpCodes.LOAD_ATTR or OpCodes.BUILD_TUPLE or OpCodes.BUILD_LIST or
               OpCodes.BUILD_MAP or OpCodes.BUILD_STRING or OpCodes.FORMAT_VALUE or OpCodes.RETURN_VALUE)
        {
            m--;
        }
        if (m - 1 < startIdx) return false;
        var marker = _instrs[m - 1].OpCode;
        if (marker is not (OpCodes.POP_TOP or OpCodes.NOP)) return false;


        for (var i = startIdx; i < m - 1; i++)
        {
            if (_instrs[i].OpCode == OpCodes.NOP) return false;
        }


        if (marker == OpCodes.POP_TOP && m - 2 >= 0 &&
            _instrs[m - 1].StartsLine.HasValue && _instrs[m - 2].StartsLine == _instrs[m - 1].StartsLine)
        {
            return false;
        }


        var len = term - m + 1;
        for (var s = endIdx; s + len <= _instrs.Count; s++)
        {
            if (!MatchesInstructionSequence(m, s, len)) continue;
            markerIdx = m - 1;
            exitStart = m;
            exitLen = len;
            _pendingBreakExitStart = m;
            _pendingBreakExitLength = len;
            return true;
        }
        return false;
    }

        private bool TryDecompileDuplicatedTailConditional(
        int idx, int endIdx, SymbolicStack stack, List<StmtNode> stmts, out int nextIdx)
    {
        nextIdx = idx;
        if (idx + 1 >= _instrs.Count) return false;


        if (IsPre312()) return false;
        var ins = _instrs[idx];
        if (ins.OpCode is not (OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or
                               OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE))
        {
            return false;
        }
        if (stack.Count == 0) return false;

        var baseStack = stack.Clone();
        if (!TryGetBranchInfo(ins, baseStack, out var condition, out var jumpIfTrue, out var targetOffset))
        {
            return false;
        }

        var fallOffset = _instrs[idx + 1].Offset;
        var t0 = jumpIfTrue ? targetOffset : fallOffset;
        var f0 = jumpIfTrue ? fallOffset : targetOffset;
        var thenStartOffset = Math.Min(t0, f0);
        var elseStartOffset = Math.Max(t0, f0);
        if (thenStartOffset == elseStartOffset) return false;
        if (!_offsetToIndex.TryGetValue(thenStartOffset, out var thenStartIdx)) return false;
        if (!_offsetToIndex.TryGetValue(elseStartOffset, out var elseStartIdx)) return false;
        if (thenStartIdx <= idx || elseStartIdx <= thenStartIdx) return false;


        var thenArmEnd = elseStartIdx;

        var elseArmEnd = -1;
        for (var k = elseStartIdx; k < endIdx && k < _instrs.Count; k++)
        {
            var op = _instrs[k].OpCode;
            if (op is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RAISE_VARARGS)
            {
                elseArmEnd = k + 1;
                break;
            }
            if (op is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
            {
                return false;
            }
        }
        if (elseArmEnd < 0) return false;

        var maxSuffix = Math.Min(thenArmEnd - thenStartIdx, elseArmEnd - elseStartIdx);
        var lcs = 0;
        for (var len = 1; len <= maxSuffix; len++)
        {
            var a = _instrs[thenArmEnd - len];
            var b = _instrs[elseArmEnd - len];
            if (a.OpCode != b.OpCode) break;
            if (a.TargetOffset.HasValue || b.TargetOffset.HasValue) break;
            if (!Equals(a.ArgVal, b.ArgVal)) break;
            lcs = len;
        }
        if (lcs == 0) return false;

        var thenPrefixEnd = thenArmEnd - lcs;
        var elsePrefixEnd = elseArmEnd - lcs;
        if (thenPrefixEnd <= thenStartIdx || elsePrefixEnd <= elseStartIdx) return false;

        var baseCount = baseStack.Count;
        var thenStack = baseStack.Clone();
        var elseStack = baseStack.Clone();
        List<StmtNode> thenBody;
        List<StmtNode> elseBody;
        try
        {
            thenBody = DecompileRange(thenStartIdx, thenPrefixEnd, thenStack);
            elseBody = DecompileRange(elseStartIdx, elsePrefixEnd, elseStack);
        }
        catch (Exception ex) when (ex is not FailFastException)
        {
            return false;
        }
        if (thenBody.Count != 0 || thenStack.Count != baseCount + 1) return false;
        if (elseBody.Count != 0 || elseStack.Count != baseCount + 1) return false;


        var commitStack = stack.Clone();
        if (!TryGetBranchInfo(ins, commitStack, out _, out _, out _)) return false;
        commitStack.Push(new TernaryExpr(condition, thenStack.Pop(), elseStack.Pop()));
        List<StmtNode> suffixStmts;
        try
        {
            suffixStmts = DecompileRange(thenPrefixEnd, thenArmEnd, commitStack);
        }
        catch (Exception ex) when (ex is not FailFastException)
        {
            return false;
        }

        TryGetBranchInfo(ins, stack, out _, out _, out _);
        stack.Clear();
        for (var i = commitStack.Count - 1; i >= 0; i--)
        {
            stack.Push(commitStack.Peek(i));
        }
        stmts.AddRange(suffixStmts);

        var lastSuffixOp = _instrs[thenArmEnd - 1].OpCode;
        if (lastSuffixOp is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RAISE_VARARGS)
        {


            var nextReachable = -1;
            for (var scan = elseArmEnd; scan < endIdx && scan < _instrs.Count; scan++)
            {
                if (!_instrs[scan].IsJumpTarget) continue;
                var targeted = false;
                for (var k = 0; k < elseArmEnd && k < _instrs.Count; k++)
                {
                    if ((_instrs[k].TargetOffset ?? -1) == _instrs[scan].Offset)
                    {
                        targeted = true;
                        break;
                    }
                }
                if (targeted)
                {
                    nextReachable = scan;
                    break;
                }
            }
            nextIdx = nextReachable >= 0 ? nextReachable : endIdx;
        }
        else
        {
            nextIdx = elseArmEnd;
        }
        return true;
    }


    private bool TryDecompileTernaryExpr(int idx, int endIdx, SymbolicStack stack, out int nextIdx)
    {
        nextIdx = idx;
        if (!IsPre312()) return false;
        if (idx >= endIdx || stack.Count == 0) return false;
        var ins = _instrs[idx];
        if (ins.OpCode != OpCodes.POP_JUMP_IF_FALSE) return false;
        if (_instrs[idx].TargetOffset is not int elseTarget ||
            !_offsetToIndex.TryGetValue(elseTarget, out var elseIdx)) return false;

        var jumpIdx = -1;
        for (var p = idx + 1; p < elseIdx; p++)
        {
            if (_instrs[p].OpCode == OpCodes.JUMP_FORWARD) { jumpIdx = p; break; }
        }
        if (jumpIdx < 0) return false;
        if (_instrs[jumpIdx].TargetOffset is not int joinTarget ||
            !_offsetToIndex.TryGetValue(joinTarget, out var joinIdx)) return false;
        if (joinIdx <= elseIdx) return false;

        ExprNode? thenExpr = null;
        var thenStack = new SymbolicStack();
        try { DecompileRange(idx + 1, jumpIdx, thenStack); }
        catch (Exception) { thenStack.Clear(); }
        if (thenStack.Count == 1)
        {
            thenExpr = thenStack.Pop();
        }
        else
        {


            for (var p = idx + 1; p + 3 < jumpIdx; p++)
            {
                if (_instrs[p].OpCode != OpCodes.COPY || (_instrs[p].OpArg ?? 0) != 1) continue;
                if (_instrs[p + 1].OpCode is not (OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)) continue;
                if (_instrs[p + 2].OpCode != OpCodes.POP_TOP) continue;
                var leftStack = new SymbolicStack();
                var rightStack = new SymbolicStack();
                try
                {
                    DecompileRange(idx + 1, p, leftStack);
                    DecompileRange(p + 3, jumpIdx, rightStack);
                }
                catch (Exception) { break; }
                if (leftStack.Count != 1 || rightStack.Count != 1) break;
                var op = _instrs[p + 1].OpCode == OpCodes.POP_JUMP_IF_FALSE ? "and" : "or";
                thenExpr = new BinaryExpr(leftStack.Pop(), op, rightStack.Pop());
                break;
            }
            if (thenExpr == null) return false;
        }

        var elseStack = new SymbolicStack();
        try { DecompileRange(elseIdx, joinIdx, elseStack); }
        catch (Exception) { return false; }
        if (elseStack.Count != 1) return false;

        var cond = stack.Peek();
        stack.Pop();
        stack.Push(new TernaryExpr(cond, thenExpr!, elseStack.Pop()));
        nextIdx = joinIdx;
        return true;
    }

    private bool TryDecompileConditional(int idx, int endIdx, SymbolicStack stack, out StmtNode? stmt, out int nextIdx)
    {
        stmt = null;
        nextIdx = idx;
        if (Environment.GetEnvironmentVariable("PYCRUNE_TRACE") == "1")
            Console.Error.WriteLine($"[trace] EnterConditional idx={idx} off={_instrs[idx].Offset} op={_instrs[idx].OpCode} stack={stack.Count} end={endIdx}");

        var isChained = TryMatchChainedBranch(idx, stack, out var cbCond, out var cbTrue, out var cbFalse, out var cbNextIdx, out var isAssert);
        if (isChained && isAssert)
        {
            stmt = new AssertStmt(cbCond, null);
            nextIdx = cbNextIdx;
            return true;
        }

        ExprNode condition;
        bool jumpIfTrue;
        int targetOffset;
        int t0;
        int f0;
        int startCIdx;
        bool isTernaryCond = false;
        PythonInstruction? ins = null;

        if (isChained)
        {
            condition = cbCond;
            jumpIfTrue = true;
            targetOffset = cbTrue;
            t0 = cbTrue;
            f0 = cbFalse;
            startCIdx = cbNextIdx;
        }
        else
        {
            ins = _instrs[idx];
            if (!TryGetBranchInfo(ins, stack, out condition, out jumpIfTrue, out targetOffset))
            {
                if (Environment.GetEnvironmentVariable("PYCRUNE_TRACE") == "1")
                    Console.Error.WriteLine($"[trace] Conditional bail: branchinfo idx={idx} stack={stack.Count}");
                return false;
            }


        if (!isChained && !jumpIfTrue && IsActiveLoopHeader(targetOffset) && idx + 1 < _instrs.Count)
        {
            var btIdx = idx + 1;
            var isBreak = false;
            if (btIdx < _instrs.Count && _instrs[btIdx].OpCode == OpCodes.POP_TOP &&
                btIdx + 1 < _instrs.Count && _instrs[btIdx + 1].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD)
            {
                var bTarget = _instrs[btIdx + 1].TargetOffset ?? -1;
                if (_activeLoopExits.Contains(bTarget)) isBreak = true;
            }
            else if (btIdx < _instrs.Count && _instrs[btIdx].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD)
            {
                var bTarget = _instrs[btIdx].TargetOffset ?? -1;
                if (_activeLoopExits.Contains(bTarget)) isBreak = true;
            }

            if (isBreak)
            {
                stmt = new IfStmt(condition, [new BreakStmt()], []);
                nextIdx = endIdx;
                return true;
            }

            var tailStack = stack.Clone();
            var tailBody = DecompileRange(btIdx, endIdx, tailStack);
            stmt = new IfStmt(condition, tailBody, []);
            nextIdx = endIdx;
            return true;
        }

        var trueLatchScan = idx + 1;
        while (trueLatchScan < _instrs.Count && _instrs[trueLatchScan].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) trueLatchScan++;
        if (jumpIfTrue && trueLatchScan < _instrs.Count && _instrs[trueLatchScan].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
        {
            var backwardTarget = _instrs[trueLatchScan].TargetOffset ?? -1;
            if (IsActiveLoopHeader(backwardTarget) && _offsetToIndex.TryGetValue(targetOffset, out var btIdx))
            {
                var isBreak = false;
                if (btIdx < _instrs.Count && _instrs[btIdx].OpCode == OpCodes.POP_TOP)
                {
                    if (btIdx + 1 < _instrs.Count && _instrs[btIdx + 1].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD)
                    {
                        var bTarget = _instrs[btIdx + 1].TargetOffset ?? -1;
                        if (_activeLoopExits.Contains(bTarget)) isBreak = true;
                    }
                }
                else if (btIdx < _instrs.Count && _instrs[btIdx].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD)
                {
                    var bTarget = _instrs[btIdx].TargetOffset ?? -1;
                    if (_activeLoopExits.Contains(bTarget)) isBreak = true;
                }

                if (isBreak)
                {
                    stmt = new IfStmt(condition, [new BreakStmt()], []);
                    nextIdx = endIdx;
                    return true;
                }


                var brBodyEnd = Math.Min(endIdx, _instrs.Count);
                if (btIdx < brBodyEnd && TryMatchBreakPath(btIdx, brBodyEnd, out var breakMarkerIdx, out _, out _))
                {
                    var brStack = stack.Clone();
                    var brBody = breakMarkerIdx > btIdx
                        ? DecompileRange(btIdx, breakMarkerIdx, brStack)
                        : new List<StmtNode>();
                    brBody.Add(new BreakStmt());
                    stmt = new IfStmt(condition, brBody, []);
                    nextIdx = endIdx;
                    return true;
                }


                if (!IsOffsetProtected(_instrs[trueLatchScan].Offset))
                {
                    var bodyEnd = Math.Min(endIdx, _instrs.Count);
                    if (btIdx < bodyEnd)
                    {
                        var bodyStack = stack.Clone();
                        var ifBody = DecompileRange(btIdx, bodyEnd, bodyStack);
                        stmt = new IfStmt(condition, ifBody, []);
                        nextIdx = bodyEnd;
                        return true;
                    }
                }
            }
        }

        if (idx + 1 < _instrs.Count && _instrs[idx + 1].OpCode == OpCodes.JUMP_FORWARD)
        {
            var jumpBranchOffset = targetOffset;
            var fallBranchOffset = ResolveJumpTarget(_instrs[idx + 1].TargetOffset ?? 0);

            if (_offsetToIndex.TryGetValue(jumpBranchOffset, out var jumpBranchIdx) &&
                _offsetToIndex.TryGetValue(fallBranchOffset, out var fallBranchIdx))
            {
                var firstIdx = Math.Min(jumpBranchIdx, fallBranchIdx);
                var secondIdx = Math.Max(jumpBranchIdx, fallBranchIdx);

                var secondOffset = _instrs[secondIdx].Offset;
                var hasJumpToSecond = false;
                for (var k = firstIdx; k < secondIdx - 1; k++)
                {
                    if (_instrs[k].TargetOffset == secondOffset)
                    {
                        hasJumpToSecond = true;
                        break;
                    }
                }

                if (!hasJumpToSecond && firstIdx < secondIdx - 1 && secondIdx > 0 && _instrs[secondIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
                {
                    var joinOffset = ResolveJumpTarget(_instrs[secondIdx - 1].TargetOffset ?? 0);
                    if (_offsetToIndex.TryGetValue(joinOffset, out var joinIdx) && joinIdx >= secondIdx)
                    {


                        var nestedCondChain =
                            !jumpIfTrue &&
                            jumpBranchIdx < fallBranchIdx &&
                            idx + 4 < _instrs.Count &&
                            ins != null &&
                            ins.TargetOffset == _instrs[idx + 2].Offset &&
                            _instrs[idx + 1].TargetOffset == _instrs[idx + 3].Offset &&
                            _instrs[idx + 2].OpCode == OpCodes.JUMP_FORWARD &&
                            _instrs[idx + 3].OpCode == OpCodes.JUMP_FORWARD &&
                            _instrs[idx + 2].TargetOffset == _instrs[idx + 4].Offset &&
                            jumpBranchOffset == _instrs[idx + 4].Offset &&
                            fallBranchOffset == _instrs[idx + 3].TargetOffset;

                        var firstStack = stack.Clone();
                        var firstBody = DecompileRange(firstIdx, secondIdx - 1, firstStack);
                        var secondStack = stack.Clone();
                        var secondBody = DecompileRange(secondIdx, joinIdx, secondStack);

                        var isJumpFirst = jumpBranchIdx < fallBranchIdx;
                        var jumpStack = isJumpFirst ? firstStack : secondStack;
                        var fallStack = isJumpFirst ? secondStack : firstStack;
                        var jumpBody = isJumpFirst ? firstBody : secondBody;
                        var fallBody = isJumpFirst ? secondBody : firstBody;

                        var trueStack = jumpIfTrue ? jumpStack : fallStack;
                        var falseStack = jumpIfTrue ? fallStack : jumpStack;
                        var trueBody = jumpIfTrue ? jumpBody : fallBody;
                        var falseBody = jumpIfTrue ? fallBody : jumpBody;

                        if (nestedCondChain)
                        {


                            condition = new TernaryExpr(
                                new TernaryExpr(condition, new ConstantExpr(false), new ConstantExpr(true)),
                                new ConstantExpr(true),
                                new ConstantExpr(false));
                            trueStack = jumpStack;
                            falseStack = fallStack;
                            trueBody = jumpBody;
                            falseBody = fallBody;
                        }

                        var baseCnt = stack.Count;
                        if (trueBody.Count == 0 && falseBody.Count == 0 &&
                            trueStack.Count > baseCnt && falseStack.Count > baseCnt)
                        {
                            var trueVal = trueStack.Pop();
                            var falseVal = falseStack.Pop();
                            stack.Push(new TernaryExpr(condition, trueVal, falseVal));
                            nextIdx = joinIdx;
                            return true;
                        }

                        stmt = new IfStmt(condition, trueBody, falseBody);
                        nextIdx = joinIdx;
                        return true;
                    }
                }
            }
        }

            if (!_offsetToIndex.TryGetValue(targetOffset, out var targetIdx))
            {
                if (Environment.GetEnvironmentVariable("PYCRUNE_TRACE") == "1")
                    Console.Error.WriteLine($"[trace] Conditional bail: target idx={idx} target={targetOffset}");
                return false;
            }

            startCIdx = idx + 1;
            t0 = jumpIfTrue ? targetOffset : (idx + 1 < _instrs.Count ? _instrs[idx + 1].Offset : 0);
            f0 = jumpIfTrue ? (idx + 1 < _instrs.Count ? _instrs[idx + 1].Offset : 0) : targetOffset;

            if (!jumpIfTrue &&
                idx + 2 < _instrs.Count &&
                _instrs[idx + 1].OpCode == OpCodes.JUMP_FORWARD &&
                _instrs[idx + 2].OpCode == OpCodes.JUMP_FORWARD &&
                ins != null && ins.TargetOffset == _instrs[idx + 2].Offset &&
                idx + 3 < _instrs.Count &&
                _instrs[idx + 1].TargetOffset == _instrs[idx + 3].Offset)
            {
                isTernaryCond = true;
                condition = new TernaryExpr(condition, new NameExpr("True"), new NameExpr("False"));
                t0 = _instrs[idx + 1].TargetOffset ?? 0;
                f0 = _instrs[idx + 2].TargetOffset ?? 0;
                targetOffset = f0;
                _offsetToIndex.TryGetValue(targetOffset, out targetIdx);
                startCIdx = idx + 3;
            }
        }


        if (!isChained && t0 == f0)
        {
            stmt = new IfStmt(condition, [new PassStmt()], []);
            nextIdx = _offsetToIndex.TryGetValue(t0, out var noOpNextIdx) ? noOpNextIdx : idx + 1;
            return true;
        }

        var jumps = new List<ConditionJump>();
        var jumpStartToIndex = new Dictionary<int, int>();

        jumps.Add(new ConditionJump { InstrIndex = idx, Condition = condition, TrueOffset = t0, FalseOffset = f0 });
        jumpStartToIndex[_instrs[idx].Offset] = 0;
        if (isChained && cbNextIdx > 0 && cbNextIdx < _instrs.Count &&
            !jumpStartToIndex.ContainsKey(_instrs[cbNextIdx].Offset))
        {


            jumpStartToIndex[_instrs[cbNextIdx].Offset] = jumps.Count;
        }

        var condStack = new SymbolicStack();
        var prevJumpIdx = isTernaryCond ? idx + 2 : idx;
        var lastCondJumpIdx = idx;
        var minTrueTarget = !isChained && jumpIfTrue && targetOffset > _instrs[idx].Offset ? targetOffset : int.MaxValue;

        for (var cIdx = startCIdx; cIdx < endIdx; cIdx++)
        {


            if (_instrs[cIdx].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                _instrs[cIdx].TargetOffset is int loopJumpTarget &&
                (IsActiveLoopHeader(loopJumpTarget) || _activeLoopExits.Contains(loopJumpTarget)))
            {
                break;
            }

            if (cIdx == prevJumpIdx + 1)
            {
                jumpStartToIndex[_instrs[cIdx].Offset] = jumps.Count;
            }

            if (TryMatchChainedBranch(cIdx, condStack, out var cbSubCond, out var cbSubTrue, out var cbSubFalse, out var cbSubNext, out _))
            {
                jumpStartToIndex[_instrs[cIdx].Offset] = jumps.Count;
                jumps.Add(new ConditionJump
                {
                    InstrIndex = cIdx,
                    Condition = cbSubCond,
                    TrueOffset = cbSubTrue,
                    FalseOffset = cbSubFalse
                });
                lastCondJumpIdx = cIdx;


                if (!jumpStartToIndex.ContainsKey(cbSubTrue))
                {
                    jumpStartToIndex[cbSubTrue] = jumps.Count;
                }
                cIdx = cbSubNext - 1;
                continue;
            }

            if (TryDecompileChainedComparison(cIdx, condStack, out var ccExpr, out var ccNext))
            {
                condStack.Push(ccExpr);
                cIdx = ccNext - 1;
                continue;
            }
            if (TryDecompileWalrus(ref cIdx, condStack))
            {
                cIdx--;
                continue;
            }

            var cIns = _instrs[cIdx];
            if (cIns.Offset >= minTrueTarget)
            {
                break;
            }
            if (cIns.OpCode is OpCodes.STORE_NAME or OpCodes.STORE_FAST or OpCodes.STORE_GLOBAL or
                OpCodes.STORE_DEREF or OpCodes.STORE_SUBSCR or OpCodes.STORE_ATTR or
                OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.JUMP_FORWARD or
                OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT or
                OpCodes.POP_TOP or OpCodes.RAISE_VARARGS or OpCodes.RERAISE)
            {


                break;
            }

            if (TryGetBranchInfo(cIns, condStack, out var cCond, out var cJumpIfTrue, out var cTargetOffset))
            {
                if (condStack.Count != 0)
                {
                    break;
                }
                if (!cJumpIfTrue &&
                    cIdx + 2 < _instrs.Count &&
                    _instrs[cIdx + 1].OpCode == OpCodes.JUMP_FORWARD &&
                    _instrs[cIdx + 2].OpCode == OpCodes.JUMP_FORWARD &&
                    cIns.TargetOffset == _instrs[cIdx + 2].Offset &&
                    cIdx + 3 < _instrs.Count &&
                    _instrs[cIdx + 1].TargetOffset == _instrs[cIdx + 3].Offset)
                {
                    cCond = new TernaryExpr(cCond, new NameExpr("True"), new NameExpr("False"));
                    var tct = _instrs[cIdx + 1].TargetOffset ?? 0;
                    var tcf = _instrs[cIdx + 2].TargetOffset ?? 0;

                    jumpStartToIndex[_instrs[cIdx].Offset] = jumps.Count;
                    jumps.Add(new ConditionJump
                    {
                        InstrIndex = cIdx,
                        Condition = cCond,
                        TrueOffset = tct,
                        FalseOffset = tcf
                    });
                    prevJumpIdx = cIdx + 2;
                    lastCondJumpIdx = cIdx;
                    cIdx += 2;
                    continue;
                }
                if (cTargetOffset < targetOffset &&
                    _offsetToIndex.TryGetValue(cTargetOffset, out var ctIdx) &&
                    ctIdx > cIdx &&
                    HasStatementOpcodeBetween(_instrs, cIdx + 1, ctIdx))
                {
                    break;
                }
                if (cJumpIfTrue && cTargetOffset > cIns.Offset && cTargetOffset < minTrueTarget)
                {
                    minTrueTarget = cTargetOffset;
                }
                var cFallOffset = cIdx + 1 < _instrs.Count ? _instrs[cIdx + 1].Offset : 0;
                var ct = cJumpIfTrue ? cTargetOffset : cFallOffset;
                var cf = cJumpIfTrue ? cFallOffset : cTargetOffset;

                jumpStartToIndex[_instrs[cIdx].Offset] = jumps.Count;
                jumps.Add(new ConditionJump
                {
                    InstrIndex = cIdx,
                    Condition = cCond,
                    TrueOffset = ct,
                    FalseOffset = cf
                });
                prevJumpIdx = cIdx;
                lastCondJumpIdx = cIdx;
                continue;
            }

            try
            {
                ExecuteInstruction(cIns, condStack, null);
            }
            catch (Exception ex) when (ex is not FailFastException)
            {
                break;
            }
        }

        var lastJump = jumps[^1];
        var thenStartOffset = Math.Min(lastJump.TrueOffset, lastJump.FalseOffset);
        var elseStartOffset = Math.Max(lastJump.TrueOffset, lastJump.FalseOffset);

        foreach (var jmp in jumps)
        {
            if (jmp.TrueOffset > thenStartOffset && jmp.TrueOffset > elseStartOffset) elseStartOffset = jmp.TrueOffset;
            if (jmp.FalseOffset > thenStartOffset && jmp.FalseOffset > elseStartOffset) elseStartOffset = jmp.FalseOffset;
        }

        condition = BuildConditionExpr(0, thenStartOffset, elseStartOffset, jumps, jumpStartToIndex);
        if (Environment.GetEnvironmentVariable("PYCRUNE_TRACE") == "1")
        {
            Console.Error.WriteLine($"[trace] TryDecompileConditional idx={idx} off={_instrs[idx].Offset} then={thenStartOffset} else={elseStartOffset} jumps={jumps.Count} cond={PythonEmitter.EmitExpr(condition)}");
            foreach (var jj in jumps) Console.Error.WriteLine($"   jump#{jj.InstrIndex} off={_instrs[jj.InstrIndex].Offset} t={jj.TrueOffset} f={jj.FalseOffset} cond={PythonEmitter.EmitExpr(jj.Condition)}");
        }

        if (!_offsetToIndex.TryGetValue(thenStartOffset, out var thenStartIdx)) return false;
        if (!_offsetToIndex.TryGetValue(elseStartOffset, out var elseStartIdx)) return false;

        var thenEndIdx = elseStartIdx;
        var elseEndIdx = elseStartIdx;

        var hasElse = false;
        if (thenEndIdx > thenStartIdx && _instrs[thenEndIdx - 1].OpCode == OpCodes.JUMP_FORWARD)
        {
            var jumpTarget = _instrs[thenEndIdx - 1].TargetOffset ?? 0;
            if (_activeLoopExits.Contains(jumpTarget) || IsActiveLoopHeader(jumpTarget))
            {
                hasElse = false;
            }
            else if (_offsetToIndex.TryGetValue(jumpTarget, out var jTargetIdx) && jTargetIdx > elseStartIdx)
            {
                hasElse = true;
                thenEndIdx--;
                elseEndIdx = jTargetIdx;
            }
        }
        else if (thenEndIdx > thenStartIdx && _instrs[thenEndIdx - 1].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
        {
            var jumpTarget = _instrs[thenEndIdx - 1].TargetOffset ?? 0;
            if (IsActiveLoopHeader(jumpTarget) || _activeLoopExits.Contains(jumpTarget))
            {
                hasElse = false;
            }
            else if (elseStartIdx < endIdx)
            {
                hasElse = true;
                thenEndIdx--;
                elseEndIdx = endIdx;
            }
        }
        else if (thenEndIdx > thenStartIdx && _instrs[thenEndIdx - 1].OpCode == OpCodes.RERAISE && elseStartIdx < endIdx)
        {
            if (thenEndIdx >= 2 && _instrs[thenEndIdx - 2].OpCode == OpCodes.POP_EXCEPT)
            {


                var exitScan = thenEndIdx - 1;
                while (exitScan > thenStartIdx &&
                       _instrs[exitScan].OpCode is OpCodes.RERAISE or OpCodes.POP_EXCEPT or OpCodes.COPY or OpCodes.EXTENDED_ARG)
                {
                    exitScan--;
                }
                if (exitScan > thenStartIdx && _instrs[exitScan].OpCode == OpCodes.JUMP_FORWARD &&
                    _offsetToIndex.TryGetValue(_instrs[exitScan].TargetOffset ?? 0, out var cleanupElseIdx) &&
                    cleanupElseIdx > elseStartIdx)
                {
                    hasElse = true;
                    thenEndIdx = exitScan;
                    elseEndIdx = cleanupElseIdx;
                }
            }
            else
            {
                hasElse = true;
                elseEndIdx = endIdx;
            }
        }
        else if (thenEndIdx > thenStartIdx &&
                 _instrs[thenEndIdx - 1].OpCode is OpCodes.RAISE_VARARGS or OpCodes.RERAISE or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST &&
                 elseStartIdx < endIdx && endIdx < _instrs.Count && _instrs[endIdx].OpCode == OpCodes.NOP)
        {


            hasElse = true;
            elseEndIdx = endIdx;
        }

        var targetIdx2 = elseEndIdx;
        while (targetIdx2 < _instrs.Count && _instrs[targetIdx2].OpCode == OpCodes.EXTENDED_ARG)
        {
            targetIdx2++;
        }
        var targetIns = targetIdx2 < _instrs.Count ? _instrs[targetIdx2] : null;
        var isExpressionContext = targetIns != null && targetIns.OpCode is
            OpCodes.STORE_NAME or OpCodes.STORE_FAST or OpCodes.STORE_GLOBAL or
            OpCodes.STORE_DEREF or OpCodes.STORE_SUBSCR or OpCodes.STORE_ATTR or
            OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or
            OpCodes.LIST_APPEND or OpCodes.SET_ADD or OpCodes.MAP_ADD or OpCodes.YIELD_VALUE;

        var actualThenEndIdx = isExpressionContext ? thenEndIdx : Math.Min(thenEndIdx, endIdx);
        var actualElseEndIdx = Math.Min(elseEndIdx, endIdx);


        if (!hasElse && TryMatchBreakPath(thenStartIdx, actualThenEndIdx, out var breakMarkerIdx2, out _, out _))
        {
            var brkStack = stack.Clone();
            var brkBody = breakMarkerIdx2 > thenStartIdx
                ? DecompileRange(thenStartIdx, breakMarkerIdx2, brkStack)
                : new List<StmtNode>();
            brkBody.Add(new BreakStmt());
            stmt = new IfStmt(condition, brkBody, []);
            nextIdx = Math.Max(elseStartIdx, thenStartIdx);
            return true;
        }

        var baseCount = stack.Count;
        var thenStack = stack.Clone();
        var thenBody = DecompileRange(thenStartIdx, actualThenEndIdx, thenStack);

        var elseStack = stack.Clone();
        var elseBody = hasElse && elseStartIdx < actualElseEndIdx
            ? DecompileRange(elseStartIdx, actualElseEndIdx, elseStack)
            : [];

        if (thenBody.Count == 0 && thenStack.Count == baseCount + 1 &&
            elseBody.Count == 0 && elseStack.Count == baseCount + 1)
        {
            var tVal = thenStack.Pop();
            var fVal = elseStack.Pop();
            var ternary = new TernaryExpr(condition, tVal, fVal);
            stack.Push(ternary);
            nextIdx = elseEndIdx;
            return true;
        }


        if (baseCount > 0 && thenBody.Count == 1 &&
            thenBody[0] is ReturnStmt { FromReturnConst: false } tRet &&
            tRet.Value is TupleExpr tTup)
        {
            var probeElse = elseBody;
            var probeConsumedToEnd = false;
            if (probeElse.Count == 0 && elseStartIdx < endIdx)
            {
                probeElse = DecompileRange(elseStartIdx, endIdx, stack.Clone());
                probeConsumedToEnd = true;
            }

            if (probeElse.Count == 1 && probeElse[0] is ReturnStmt { FromReturnConst: false } eRet &&
                eRet.Value is TupleExpr eTup &&
                tTup.Elements.Count == eTup.Elements.Count && tTup.Elements.Count > baseCount)
            {
                var prefixOk = true;
                for (var bi = 0; bi < baseCount; bi++)
                {
                    var baseVal = stack.Peek(baseCount - 1 - bi);
                    if (!ExprEquals(tTup.Elements[bi], baseVal) || !ExprEquals(eTup.Elements[bi], baseVal))
                    {
                        prefixOk = false;
                        break;
                    }
                }

                if (prefixOk)
                {
                    var diffIdx = baseCount;
                    while (diffIdx < tTup.Elements.Count && ExprEquals(tTup.Elements[diffIdx], eTup.Elements[diffIdx]))
                    {
                        diffIdx++;
                    }
                    var restOk = true;
                    for (var j = diffIdx + 1; j < tTup.Elements.Count; j++)
                    {
                        if (!ExprEquals(tTup.Elements[j], eTup.Elements[j])) { restOk = false; break; }
                    }

                    if (restOk && diffIdx < tTup.Elements.Count)
                    {
                        var elems = new List<ExprNode>();
                        for (var j = 0; j < diffIdx; j++) elems.Add(tTup.Elements[j]);
                        elems.Add(new TernaryExpr(condition, tTup.Elements[diffIdx], eTup.Elements[diffIdx]));
                        for (var j = diffIdx + 1; j < tTup.Elements.Count; j++) elems.Add(tTup.Elements[j]);
                        stmt = new ReturnStmt(new TupleExpr(elems));
                        nextIdx = probeConsumedToEnd
                            ? endIdx
                            : (hasElse ? Math.Min(elseEndIdx, endIdx) : Math.Min(elseStartIdx, endIdx));
                        return true;
                    }
                }
            }
        }

        if (thenBody.Count == 1 && elseBody.Count == 0 &&
            thenBody[0] is RaiseStmt { Exception: var raiseExc } &&
            (raiseExc is AssertionErrorExpr || (raiseExc is CallExpr ce && ce.Function is AssertionErrorExpr)))
        {
            ExprNode? assertMsg = null;
            if (raiseExc is CallExpr callEx && callEx.Arguments.Count > 0)
            {
                assertMsg = callEx.Arguments[0];
            }
            stmt = new AssertStmt(InvertCondition(condition), assertMsg);
            nextIdx = hasElse ? Math.Min(elseEndIdx, endIdx) : Math.Min(elseStartIdx, endIdx);
            return true;
        }

        if (thenBody.Count == 1 && elseBody.Count == 0 &&
            thenBody[0] is AssertStmt { Test: ConstantExpr { Value: false } } falseAssert &&
            _instrs[lastJump.InstrIndex].OpCode == OpCodes.POP_JUMP_IF_TRUE)
        {


            stmt = new AssertStmt(InvertCondition(condition), falseAssert.Message);
            nextIdx = hasElse ? Math.Min(elseEndIdx, endIdx) : Math.Min(elseStartIdx, endIdx);
            return true;
        }


        if (hasElse && elseBody.Count == 0 && elseStartIdx < actualElseEndIdx)
        {
            var onlyNops = true;
            for (var k = elseStartIdx; k < actualElseEndIdx && k < _instrs.Count; k++)
            {
                if (_instrs[k].OpCode is not (OpCodes.NOP or OpCodes.EXTENDED_ARG))
                {
                    onlyNops = false;
                    break;
                }
            }
            if (onlyNops) elseBody = [new PassStmt()];
        }

        stmt = new IfStmt(condition, thenBody, elseBody);
        nextIdx = hasElse ? Math.Min(elseEndIdx, endIdx) : Math.Min(elseStartIdx, endIdx);
        return true;
    }

    private sealed class ConditionJump
    {
        public int InstrIndex { get; set; }
        public ExprNode Condition { get; set; } = null!;
        public int TrueOffset { get; set; }
        public int FalseOffset { get; set; }
    }

    private ExprNode InvertCondition(ExprNode expr)
    {
        if (expr is UnaryExpr u && u.Op == "not")
        {
            return u.Operand;
        }


        if (expr is CompareExpr { Comparators.Count: 1 } cmp)
        {
            var (op, right) = cmp.Comparators[0];
            var flipped = op switch
            {
                "is" => "is not",
                "is not" => "is",
                "in" => "not in",
                "not in" => "in",
                _ => null
            };
            if (flipped != null)
            {
                return new CompareExpr(cmp.Left, [(flipped, right)]);
            }
        }


        if (expr is BoolOpExpr b && b.Values.Count > 0)
        {
            var invertedOp = b.Op == "and" ? "or" : "and";
            var flat = new List<ExprNode>();
            foreach (var v in b.Values)
            {
                var iv = InvertCondition(v);
                if (iv is BoolOpExpr vb && vb.Op == invertedOp)
                {
                    flat.AddRange(vb.Values);
                }
                else
                {
                    flat.Add(iv);
                }
            }
            return new BoolOpExpr(invertedOp, flat);
        }

        return new UnaryExpr("not", expr);
    }

    private static bool ExprEquals(ExprNode? a, ExprNode? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        switch (a, b)
        {
            case (ConstantExpr ca, ConstantExpr cb):
                return Equals(ca.Value, cb.Value);
            case (NameExpr na, NameExpr nb):
                return na.Name == nb.Name;
            case (UnaryExpr ua, UnaryExpr ub):
                return ua.Op == ub.Op && ExprEquals(ua.Operand, ub.Operand);
            case (BinaryExpr ba, BinaryExpr bb):
                return ba.Op == bb.Op && ExprEquals(ba.Left, bb.Left) && ExprEquals(ba.Right, bb.Right);
            case (BoolOpExpr oa, BoolOpExpr ob):
                if (oa.Op != ob.Op || oa.Values.Count != ob.Values.Count) return false;
                for (var i = 0; i < oa.Values.Count; i++)
                {
                    if (!ExprEquals(oa.Values[i], ob.Values[i])) return false;
                }
                return true;
            case (CompareExpr cpa, CompareExpr cpb):
                if (!ExprEquals(cpa.Left, cpb.Left) || cpa.Comparators.Count != cpb.Comparators.Count) return false;
                for (var i = 0; i < cpa.Comparators.Count; i++)
                {
                    if (cpa.Comparators[i].Op != cpb.Comparators[i].Op ||
                        !ExprEquals(cpa.Comparators[i].Right, cpb.Comparators[i].Right)) return false;
                }
                return true;
            case (TernaryExpr ta, TernaryExpr tb):
                return ExprEquals(ta.Condition, tb.Condition) && ExprEquals(ta.IfTrue, tb.IfTrue) && ExprEquals(ta.IfFalse, tb.IfFalse);
            case (CallExpr fa, CallExpr fb):
                if (!ExprEquals(fa.Function, fb.Function) ||
                    fa.Arguments.Count != fb.Arguments.Count ||
                    fa.KeywordArguments.Count != fb.KeywordArguments.Count) return false;
                for (var i = 0; i < fa.Arguments.Count; i++)
                {
                    if (!ExprEquals(fa.Arguments[i], fb.Arguments[i])) return false;
                }
                for (var i = 0; i < fa.KeywordArguments.Count; i++)
                {
                    if (fa.KeywordArguments[i].Name != fb.KeywordArguments[i].Name ||
                        !ExprEquals(fa.KeywordArguments[i].Value, fb.KeywordArguments[i].Value)) return false;
                }
                return true;
            case (SubscriptExpr sa, SubscriptExpr sb):
                return ExprEquals(sa.Target, sb.Target) && ExprEquals(sa.Index, sb.Index);
            case (AttributeExpr aa, AttributeExpr ab):
                return aa.Attribute == ab.Attribute && ExprEquals(aa.Target, ab.Target);
            case (SliceExpr la, SliceExpr lb):
                return ExprEquals(la.Lower, lb.Lower) && ExprEquals(la.Upper, lb.Upper) && ExprEquals(la.Step, lb.Step);
            case (TupleExpr xa, TupleExpr xb):
                return ExprListEquals(xa.Elements, xb.Elements);
            case (ListExpr ya, ListExpr yb):
                return ExprListEquals(ya.Elements, yb.Elements);
            case (SetExpr za, SetExpr zb):
                return ExprListEquals(za.Elements, zb.Elements);
            case (NamedExpr nwa, NamedExpr nwb):
                return ExprEquals(nwa.Target, nwb.Target) && ExprEquals(nwa.Value, nwb.Value);
            case (DictExpr da, DictExpr db):
                if (da.Entries.Count != db.Entries.Count) return false;
                for (var i = 0; i < da.Entries.Count; i++)
                {
                    if (!ExprEquals(da.Entries[i].Key, db.Entries[i].Key) ||
                        !ExprEquals(da.Entries[i].Value, db.Entries[i].Value)) return false;
                }
                return true;
            case (StarredExpr sta, StarredExpr stb):
                return ExprEquals(sta.Value, stb.Value);
            default:
                return a.Equals(b);
        }
    }

    private static bool ExprListEquals(IReadOnlyList<ExprNode> a, IReadOnlyList<ExprNode> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!ExprEquals(a[i], b[i])) return false;
        }
        return true;
    }

    private ExprNode CombineAnd(ExprNode left, ExprNode right)
    {
        if (left is ConstantExpr { Value: false } || right is ConstantExpr { Value: false })
            return new ConstantExpr(false);
        if (left is ConstantExpr { Value: true })
            return right;
        if (right is ConstantExpr { Value: true })
            return left;


        if (right is BoolOpExpr rbOr && rbOr.Op == "or" && rbOr.Values.Count > 0 &&
            ExprEquals(InvertCondition(left), rbOr.Values[0]))
        {
            var rest = rbOr.Values.Count == 1 ? null : new BoolOpExpr("or", rbOr.Values.Skip(1).ToList());
            return rest == null ? left : CombineAnd(left, rest);
        }
        if (left is BoolOpExpr lbOr && lbOr.Op == "or" && lbOr.Values.Count > 0 &&
            ExprEquals(InvertCondition(right), lbOr.Values[0]))
        {
            var rest = lbOr.Values.Count == 1 ? null : new BoolOpExpr("or", lbOr.Values.Skip(1).ToList());
            return rest == null ? right : CombineAnd(rest, right);
        }

        var values = new List<ExprNode>();
        if (left is BoolOpExpr bLeft && bLeft.Op == "and")
        {
            values.AddRange(bLeft.Values);
        }
        else
        {
            values.Add(left);
        }

        if (right is BoolOpExpr bRight && bRight.Op == "and")
        {
            values.AddRange(bRight.Values);
        }
        else
        {
            values.Add(right);
        }

        return new BoolOpExpr("and", values);
    }

    private ExprNode CombineOr(ExprNode left, ExprNode right)
    {
        if (left is ConstantExpr { Value: true } || right is ConstantExpr { Value: true })
            return new ConstantExpr(true);
        if (left is ConstantExpr { Value: false })
            return right;
        if (right is ConstantExpr { Value: false })
            return left;


        if (right is BoolOpExpr rbAnd && rbAnd.Op == "and" && rbAnd.Values.Count > 0 &&
            ExprEquals(InvertCondition(left), rbAnd.Values[0]))
        {
            var rest = rbAnd.Values.Count == 1 ? null : new BoolOpExpr("and", rbAnd.Values.Skip(1).ToList());
            return rest == null ? left : CombineOr(left, rest);
        }
        if (left is BoolOpExpr lbAnd && lbAnd.Op == "and" && lbAnd.Values.Count > 0 &&
            ExprEquals(InvertCondition(right), lbAnd.Values[0]))
        {
            var rest = lbAnd.Values.Count == 1 ? null : new BoolOpExpr("and", lbAnd.Values.Skip(1).ToList());
            return rest == null ? right : CombineOr(rest, right);
        }

        var values = new List<ExprNode>();
        if (left is BoolOpExpr bLeft && bLeft.Op == "or")
        {
            values.AddRange(bLeft.Values);
        }
        else
        {
            values.Add(left);
        }

        if (right is BoolOpExpr bRight && bRight.Op == "or")
        {
            values.AddRange(bRight.Values);
        }
        else
        {
            values.Add(right);
        }

        return new BoolOpExpr("or", values);
    }

    private ExprNode BuildConditionExpr(int jumpIdx, int curTrueOffset, int curFalseOffset, List<ConditionJump> jumps, Dictionary<int, int> jumpStartToIndex)
    {
        if (jumpIdx >= jumps.Count) return new ConstantExpr(true);

        var j = jumps[jumpIdx];
        var cond = j.Condition;
        var t = j.TrueOffset;
        var f = j.FalseOffset;
        if (Environment.GetEnvironmentVariable("PYCRUNE_TRACE") == "1")
            Console.Error.WriteLine($"[bce] idx={jumpIdx} t={t} f={f} curT={curTrueOffset} curF={curFalseOffset} cond={PythonEmitter.EmitExpr(cond)} map=[{string.Join(",", jumpStartToIndex.Select(kv => kv.Key + ":" + kv.Value))}]");

        if (t == curTrueOffset && f == curFalseOffset)
        {
            return cond;
        }
        if (t == curFalseOffset && f == curTrueOffset)
        {
            return InvertCondition(cond);
        }
        if (f == curFalseOffset)
        {
            if (t == curTrueOffset) return cond;
            if (jumpStartToIndex.TryGetValue(t, out var ni))
            {
                return CombineAnd(cond, BuildConditionExpr(ni, curTrueOffset, curFalseOffset, jumps, jumpStartToIndex));
            }
            return new ConstantExpr(false);
        }
        if (t == curTrueOffset)
        {
            if (f == curFalseOffset) return cond;
            if (jumpStartToIndex.TryGetValue(f, out var ni))
            {
                return CombineOr(cond, BuildConditionExpr(ni, curTrueOffset, curFalseOffset, jumps, jumpStartToIndex));
            }
            return cond;
        }
        if (t == curFalseOffset)
        {
            if (f == curTrueOffset) return InvertCondition(cond);
            if (jumpStartToIndex.TryGetValue(f, out var ni))
            {
                return CombineAnd(InvertCondition(cond), BuildConditionExpr(ni, curTrueOffset, curFalseOffset, jumps, jumpStartToIndex));
            }
            return new ConstantExpr(false);
        }
        if (f == curTrueOffset)
        {
            if (t == curFalseOffset) return InvertCondition(cond);
            if (jumpStartToIndex.TryGetValue(t, out var ni))
            {
                return CombineOr(InvertCondition(cond), BuildConditionExpr(ni, curTrueOffset, curFalseOffset, jumps, jumpStartToIndex));
            }
            return InvertCondition(cond);
        }

        if (jumpStartToIndex.ContainsKey(f))
        {


            var chain = new List<ExprNode> { cond };
            var chainTrue = t;
            var chainFalse = f;
            var seen = new HashSet<int> { jumpIdx };
            while (jumpStartToIndex.TryGetValue(chainTrue, out var chainNext) &&
                   chainNext >= 0 && chainNext < jumps.Count &&
                   seen.Add(chainNext))
            {
                var nextJump = jumps[chainNext];
                if (nextJump.FalseOffset == chainFalse)
                {
                    chain.Add(nextJump.Condition);
                    chainTrue = nextJump.TrueOffset;
                }
                else if (nextJump.TrueOffset == chainFalse)
                {


                    chain.Add(InvertCondition(nextJump.Condition));
                    chainTrue = nextJump.FalseOffset;
                }
                else
                {
                    break;
                }
            }
            if (chain.Count > 1 && chainTrue == curTrueOffset &&
                jumpStartToIndex.TryGetValue(chainFalse, out var chainContinuation))
            {
                var chainExpr = new BoolOpExpr("and", chain);
                return CombineOr(chainExpr, BuildConditionExpr(chainContinuation, curTrueOffset, curFalseOffset, jumps, jumpStartToIndex));
            }

            var left = BuildConditionExpr(jumpIdx, curTrueOffset, f, jumps, jumpStartToIndex);
            var reachF = BuildConditionExpr(jumpIdx, f, curTrueOffset, jumps, jumpStartToIndex);
            var nxt = jumpStartToIndex[f];
            var fromF = BuildConditionExpr(nxt, curTrueOffset, curFalseOffset, jumps, jumpStartToIndex);
            return CombineOr(left, CombineAnd(reachF, fromF));
        }

        if (jumpStartToIndex.ContainsKey(t))
        {
            var left = BuildConditionExpr(jumpIdx, t, curFalseOffset, jumps, jumpStartToIndex);
            var nxt = jumpStartToIndex[t];
            var fromT = BuildConditionExpr(nxt, curTrueOffset, curFalseOffset, jumps, jumpStartToIndex);
            return CombineAnd(left, fromT);
        }

        return cond;
    }

    private bool TryDecompileWalrus(ref int idx, SymbolicStack stack)
    {
        var ins = _instrs[idx];
        if ((ins.OpCode == OpCodes.COPY && ins.OpArg == 1) || ins.OpCode == OpCodes.LEGACY_DUP_TOP)
        {
            if (idx > 0 && _instrs[idx - 1].OpCode is OpCodes.CALL_INTRINSIC_1 or OpCodes.CALL_INTRINSIC_2) return false;
            var next = _instrs[idx + 1];
            if (next.OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
            {
                var varName = next.ArgRepr ?? next.ArgVal?.ToString() ?? "";
                if (varName.StartsWith("NULL + ")) varName = varName[7..];
                if (varName == "__classcell__") return false;
                if (stack.Count > 0)
                {
                    var val = stack.Pop();
                    stack.Push(new NamedExpr(new NameExpr(varName), val));
                    idx += 2;
                    return true;
                }
            }
        }
        return false;
    }

    private ExprNode ExtractTarget(ref int loopIdx)
    {
        while (loopIdx < _instrs.Count && _instrs[loopIdx].OpCode == OpCodes.EXTENDED_ARG)
        {
            loopIdx++;
        }
        if (loopIdx >= _instrs.Count) return new NameExpr("__pycrune_ph_target");

        var cur = _instrs[loopIdx];
        if (cur.OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
        {
            var name = cur.ArgRepr ?? cur.ArgVal?.ToString() ?? "__pycrune_ph_x";
            if (name.StartsWith("NULL + ")) name = name[7..];
            loopIdx++;
            return new NameExpr(name);
        }
        if (cur.OpCode == OpCodes.UNPACK_SEQUENCE)
        {
            var count = cur.OpArg ?? 0;
            loopIdx++;
            var targets = new List<ExprNode>();
            for (var i = 0; i < count && loopIdx < _instrs.Count; i++)
            {
                targets.Add(ExtractTarget(ref loopIdx));
            }
            return new TupleExpr(targets);
        }
        loopIdx++;
        return new NameExpr("__pycrune_ph_target");
    }

    private bool TryDecompileInlinedComprehension(int idx, SymbolicStack stack, List<StmtNode>? stmts, out int nextIdx)
    {
        nextIdx = idx;
        if (idx >= _instrs.Count) return false;

        var clearVars = new List<string>();
        var p = idx;
        while (p < _instrs.Count && (_instrs[p].OpCode is OpCodes.LOAD_FAST_AND_CLEAR or OpCodes.MAKE_CELL))
        {
            if (_instrs[p].OpCode == OpCodes.LOAD_FAST_AND_CLEAR)
            {
                clearVars.Add(_instrs[p].ArgRepr ?? _instrs[p].ArgVal?.ToString() ?? "");
            }
            p++;
        }

        if (clearVars.Count == 0 || p + 3 >= _instrs.Count) return false;

        if (_instrs[p].OpCode != OpCodes.SWAP || _instrs[p].OpArg != clearVars.Count + 1) return false;
        var collIns = _instrs[p + 1];
        if (collIns.OpCode is not (OpCodes.BUILD_LIST or OpCodes.BUILD_SET or OpCodes.BUILD_MAP) || collIns.OpArg != 0) return false;
        if (_instrs[p + 2].OpCode != OpCodes.SWAP || _instrs[p + 2].OpArg != 2) return false;
        var forIterPos = p + 3;


        if (_instrs[forIterPos].OpCode == OpCodes.GET_AITER &&
            forIterPos + 1 < _instrs.Count && _instrs[forIterPos + 1].OpCode == OpCodes.GET_ANEXT)
        {
            forIterPos++;
        }
        var isAsync = _instrs[forIterPos].OpCode == OpCodes.GET_ANEXT;


        if (!isAsync && _instrs[forIterPos].OpCode == OpCodes.GET_ITER &&
            forIterPos + 1 < _instrs.Count && _instrs[forIterPos + 1].OpCode == OpCodes.FOR_ITER)
        {
            forIterPos++;
        }
        if (!isAsync && _instrs[forIterPos].OpCode != OpCodes.FOR_ITER) return false;

        if (stack.Count == 0)
        {
            return false;
        }
        var outerIterator = stack.Pop();
        var loopHeaderOffset = _instrs[forIterPos].Offset;

        var endForIdx = -1;
        var endForOffset = 0;
        if (isAsync)
        {
            for (var scan = forIterPos + 1; scan < _instrs.Count; scan++)
            {
                if (_instrs[scan].OpCode == OpCodes.END_ASYNC_FOR)
                {
                    endForIdx = scan;
                    endForOffset = _instrs[scan].Offset;
                    break;
                }
            }
            if (endForIdx < 0) return false;
        }
        else
        {
            var outerForIter = _instrs[forIterPos];
            endForOffset = outerForIter.TargetOffset ?? 0;
            if (!_offsetToIndex.TryGetValue(endForOffset, out endForIdx)) return false;
        }

        var loopIdx = forIterPos + 1;
        if (isAsync)
        {
            while (loopIdx < endForIdx && _instrs[loopIdx].OpCode != OpCodes.END_SEND) loopIdx++;
            if (loopIdx < endForIdx && _instrs[loopIdx].OpCode == OpCodes.END_SEND) loopIdx++;
        }
        var outerTarget = ExtractTarget(ref loopIdx);

        var clauses = new List<ComprehensionClause>();
        var curTarget = outerTarget;
        var curIterator = outerIterator;
        var curFilters = new List<ExprNode>();
        var curFilterThreaded = new List<bool>();

        var innerStack = new SymbolicStack();
        ExprNode? compResult = null;

        while (loopIdx < endForIdx)
        {
            var cur = _instrs[loopIdx];

            if (cur.OpCode == OpCodes.FOR_ITER)
            {
                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, false, curFilterThreaded));
                curFilters = [];
                curFilterThreaded = [];
                curIterator = innerStack.Pop();
                loopHeaderOffset = cur.Offset;
                loopIdx++;
                curTarget = ExtractTarget(ref loopIdx);
                continue;
            }

            if (cur.OpCode == OpCodes.GET_ANEXT)
            {
                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, true, curFilterThreaded));
                curFilters = [];
                curFilterThreaded = [];
                curIterator = innerStack.Pop();
                loopIdx++;
                while (loopIdx < endForIdx && _instrs[loopIdx].OpCode != OpCodes.END_SEND) loopIdx++;
                if (loopIdx < endForIdx && _instrs[loopIdx].OpCode == OpCodes.END_SEND) loopIdx++;
                curTarget = ExtractTarget(ref loopIdx);
                continue;
            }

            if (TryDecompileAwaitOrYieldFrom(loopIdx, innerStack, out var awaitExpr, out var awaitNextIdx))
            {
                innerStack.Push(awaitExpr);
                loopIdx = awaitNextIdx;
                continue;
            }

            if (TryDecompileInlinedComprehension(loopIdx, innerStack, null, out var nestedCompNext))
            {
                loopIdx = nestedCompNext;
                continue;
            }

            if (TryDecompileWalrus(ref loopIdx, innerStack))
            {
                continue;
            }

            if (cur.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
            {


                var backScan = loopIdx + 1;
                while (backScan < _instrs.Count && _instrs[backScan].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) backScan++;
                if (backScan < _instrs.Count &&
                    _instrs[backScan].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                    (_instrs[backScan].TargetOffset ?? -1) == loopHeaderOffset &&
                    _offsetToIndex.TryGetValue(cur.TargetOffset ?? 0, out var elementIdx) &&
                    elementIdx > backScan)
                {
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_TRUE)
                    {
                        curFilters.Add(innerStack.Pop());
                    }
                    else if (cur.OpCode == OpCodes.POP_JUMP_IF_FALSE)
                    {
                        curFilters.Add(new UnaryExpr("not", innerStack.Pop()));
                    }
                    else if (cur.OpCode == OpCodes.POP_JUMP_IF_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is", new NameExpr("None"))]));
                    }
                    else
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is not", new NameExpr("None"))]));
                    }
                    curFilterThreaded.Add(true);
                    loopIdx = elementIdx;
                    continue;
                }

                if (loopIdx + 1 < _instrs.Count && _instrs[loopIdx + 1].OpCode == OpCodes.JUMP_BACKWARD)
                {
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_TRUE)
                    {
                        curFilters.Add(innerStack.Pop());
                        curFilterThreaded.Add(true);
                        loopIdx += 2;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_NOT_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is not", new NameExpr("None"))]));
                        curFilterThreaded.Add(true);
                        loopIdx += 2;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is", new NameExpr("None"))]));
                        curFilterThreaded.Add(true);
                        loopIdx += 2;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_FALSE)
                    {
                        curFilters.Add(new UnaryExpr("not", innerStack.Pop()));
                        curFilterThreaded.Add(true);
                        loopIdx += 2;
                        continue;
                    }
                }

                var targetOff = ResolveJumpTarget(cur.TargetOffset ?? 0);
                var isLoopEndOrContinue = targetOff >= endForOffset || targetOff == loopHeaderOffset;
                if (!isLoopEndOrContinue && _offsetToIndex.TryGetValue(targetOff, out var tIdx))
                {
                    isLoopEndOrContinue = _instrs[tIdx].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.END_FOR or OpCodes.FOR_ITER;
                }

                if (isLoopEndOrContinue)
                {
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_FALSE)
                    {
                        curFilters.Add(innerStack.Pop());
                        curFilterThreaded.Add(false);
                        loopIdx++;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_TRUE)
                    {
                        curFilters.Add(new UnaryExpr("not", innerStack.Pop()));
                        curFilterThreaded.Add(false);
                        loopIdx++;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is not", new NameExpr("None"))]));
                        curFilterThreaded.Add(false);
                        loopIdx++;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_NOT_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is", new NameExpr("None"))]));
                        curFilterThreaded.Add(false);
                        loopIdx++;
                        continue;
                    }
                }
            }

            if (TryDecompileConditional(loopIdx, endForIdx, innerStack, out var ifStmt, out var ifNextIdx))
            {
                loopIdx = ifNextIdx;
                continue;
            }

            if (cur.OpCode == OpCodes.LIST_APPEND)
            {
                var elt = innerStack.Pop();
                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, isAsync, curFilterThreaded));
                compResult = new ListCompExpr(elt, clauses);
                break;
            }

            if (cur.OpCode == OpCodes.SET_ADD)
            {
                var elt = innerStack.Pop();
                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, isAsync, curFilterThreaded));
                compResult = new SetCompExpr(elt, clauses);
                break;
            }

            if (cur.OpCode == OpCodes.MAP_ADD)
            {
                var val = innerStack.Pop();
                var key = innerStack.Pop();
                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, isAsync, curFilterThreaded));
                compResult = new DictCompExpr(key, val, clauses);
                break;
            }

            ExecuteInstruction(cur, innerStack, null);
            loopIdx++;
        }

        if (compResult == null) return false;

        stack.Push(compResult);

        var postIdx = endForIdx + 1;
        if (postIdx < _instrs.Count && _instrs[postIdx].OpCode == OpCodes.JUMP_FORWARD)
        {
            var targetOff = _instrs[postIdx].TargetOffset ?? 0;
            if (_offsetToIndex.TryGetValue(targetOff, out var targetIdx))
            {
                postIdx = targetIdx;
            }
        }

        if (postIdx < _instrs.Count && _instrs[postIdx].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL &&
            !clearVars.Contains(_instrs[postIdx].ArgRepr ?? _instrs[postIdx].ArgVal?.ToString() ?? ""))
        {
            if (stmts != null)
            {
                ExecuteInstruction(_instrs[postIdx], stack, stmts);
            }
            postIdx++;
        }

        while (postIdx < _instrs.Count)
        {
            var ins = _instrs[postIdx];
            if (ins.OpCode == OpCodes.SWAP)
            {
                postIdx++;
            }
            else if (ins.OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME &&
                     clearVars.Contains(ins.ArgRepr ?? ins.ArgVal?.ToString() ?? ""))
            {
                postIdx++;
            }
            else
            {
                break;
            }
        }

        nextIdx = postIdx;
        return true;
    }

    private static bool IsComprehensionCode(PythonCodeObject code) =>
        code.Name is "<genexpr>" or "<listcomp>" or "<setcomp>" or "<dictcomp>";

    public GeneratorExpr DecompileGenExpr(ExprNode outerIter)
    {
        DecompileComprehensionParts(outerIter, out var elt, out _, out var clauses);
        if (elt == null) throw new FailFastException($"comprehension '{_code.Name}' has no element expression");
        return new GeneratorExpr(elt, clauses);
    }

        public ExprNode DecompileLegacyComprehension(ExprNode outerIter)
    {
        DecompileComprehensionParts(outerIter, out var elt, out var dictKey, out var clauses);
        if (_code.Name == "<dictcomp>")
        {
            if (dictKey == null || elt == null)
                throw new FailFastException($"dict comprehension '{_code.Name}' has no key/value expression");
            return new DictCompExpr(dictKey, elt, clauses);
        }
        if (elt == null) throw new FailFastException($"comprehension '{_code.Name}' has no element expression");
        return _code.Name switch
        {
            "<listcomp>" => new ListCompExpr(elt, clauses),
            "<setcomp>" => new SetCompExpr(elt, clauses),
            _ => new GeneratorExpr(elt, clauses)
        };
    }

    private void DecompileComprehensionParts(
        ExprNode outerIter, out ExprNode? elt, out ExprNode? dictKey, out List<ComprehensionClause> clauses)
    {
        elt = null;
        dictKey = null;
        clauses = new List<ComprehensionClause>();
        var loopIdx = 0;
        while (loopIdx < _instrs.Count && _instrs[loopIdx].OpCode is not (OpCodes.FOR_ITER or OpCodes.GET_ANEXT))
        {
            loopIdx++;
        }

        if (loopIdx >= _instrs.Count) return;

        var isAsyncStart = _instrs[loopIdx].OpCode == OpCodes.GET_ANEXT;
        var loopHeaderOffset = _instrs[loopIdx].Offset;
        var endForOffset = 0;
        int endForIdx;
        if (isAsyncStart)
        {
            var endSendIdx = loopIdx;
            while (endSendIdx < _instrs.Count && _instrs[endSendIdx].OpCode != OpCodes.END_SEND) endSendIdx++;
            if (endSendIdx < _instrs.Count)
            {


                endForIdx = endSendIdx;
                while (endForIdx < _instrs.Count && _instrs[endForIdx].OpCode != OpCodes.END_ASYNC_FOR) endForIdx++;
                if (endForIdx >= _instrs.Count) endForIdx = _instrs.Count - 1;
                endForOffset = _instrs[endForIdx].Offset;
                loopIdx = endSendIdx + 1;
            }
            else
            {


                var legacyEnd = loopIdx;
                while (legacyEnd < _instrs.Count && _instrs[legacyEnd].OpCode != OpCodes.END_ASYNC_FOR) legacyEnd++;
                if (legacyEnd >= _instrs.Count) return;
                endForIdx = legacyEnd;
                endForOffset = _instrs[legacyEnd].Offset;
                loopIdx++;
                while (loopIdx < legacyEnd &&
                       _instrs[loopIdx].OpCode is OpCodes.LOAD_CONST or OpCodes.LEGACY_YIELD_FROM or OpCodes.LEGACY_POP_BLOCK or OpCodes.NOP)
                {
                    loopIdx++;
                }


                if (loopIdx < legacyEnd && _instrs[loopIdx].OpCode == OpCodes.SEND &&
                    _instrs[loopIdx].TargetOffset is int sendBindTarget &&
                    _offsetToIndex.TryGetValue(sendBindTarget, out var sendBindIdx))
                {
                    loopIdx = sendBindIdx;
                }


                for (var s = loopIdx; s < legacyEnd; s++)
                {
                    if (_instrs[s].OpCode == OpCodes.JUMP_BACKWARD &&
                        _instrs[s].TargetOffset is int backTarget && backTarget < _instrs[s].Offset)
                    {
                        loopHeaderOffset = backTarget;
                    }
                }
            }
        }
        else
        {
            var forIterIns = _instrs[loopIdx];
            endForOffset = forIterIns.TargetOffset ?? 0;
            _offsetToIndex.TryGetValue(endForOffset, out endForIdx);
            loopIdx++;
        }
        var curAsync = isAsyncStart;
        var outerTarget = ExtractTarget(ref loopIdx);

        var curTarget = outerTarget;
        var curIterator = outerIter;
        var curFilters = new List<ExprNode>();

        var innerStack = new SymbolicStack();
        var curFilterThreaded = new List<bool>();

        var limitIdx = endForIdx > 0 ? endForIdx : _instrs.Count;
        while (loopIdx < limitIdx)
        {
            if (TryDecompileWalrus(ref loopIdx, innerStack))
            {
                continue;
            }


            if (TryDecompileShortCircuitBoolOp(ref loopIdx, limitIdx, innerStack))
            {
                loopIdx++;
                continue;
            }

            var cur = _instrs[loopIdx];

            if (cur.OpCode == OpCodes.GET_ANEXT && !isAsyncStart)
            {

                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, curAsync, curFilterThreaded));
                curFilters = [];
                curFilterThreaded = [];
                curIterator = innerStack.Pop();
                var endSendIdx = loopIdx;
                while (endSendIdx < _instrs.Count && _instrs[endSendIdx].OpCode != OpCodes.END_SEND) endSendIdx++;
                if (endSendIdx >= _instrs.Count) break;
                loopIdx = endSendIdx + 1;
                curTarget = ExtractTarget(ref loopIdx);
                curAsync = true;
                continue;
            }

            if (cur.OpCode == OpCodes.FOR_ITER)
            {
                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, curAsync, curFilterThreaded));
                curFilters = [];
                curFilterThreaded = [];
                curIterator = innerStack.Pop();
                loopHeaderOffset = cur.Offset;
                loopIdx++;
                curTarget = ExtractTarget(ref loopIdx);
                curAsync = false;
                continue;
            }

            if (cur.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)
            {


                var backScan = loopIdx + 1;
                while (backScan < _instrs.Count && _instrs[backScan].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) backScan++;
                if (backScan < _instrs.Count &&
                    _instrs[backScan].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
                    (_instrs[backScan].TargetOffset ?? -1) == loopHeaderOffset &&
                    _offsetToIndex.TryGetValue(cur.TargetOffset ?? 0, out var elementIdx) &&
                    elementIdx > backScan)
                {
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_TRUE)
                    {
                        curFilters.Add(innerStack.Pop());
                    }
                    else if (cur.OpCode == OpCodes.POP_JUMP_IF_FALSE)
                    {
                        curFilters.Add(new UnaryExpr("not", innerStack.Pop()));
                    }
                    else if (cur.OpCode == OpCodes.POP_JUMP_IF_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is", new NameExpr("None"))]));
                    }
                    else
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is not", new NameExpr("None"))]));
                    }
                    curFilterThreaded.Add(true);
                    loopIdx = elementIdx;
                    continue;
                }

                if (loopIdx + 1 < _instrs.Count && _instrs[loopIdx + 1].OpCode == OpCodes.JUMP_BACKWARD)
                {
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_TRUE)
                    {
                        curFilters.Add(innerStack.Pop());
                        curFilterThreaded.Add(true);
                        loopIdx += 2;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_NOT_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is not", new NameExpr("None"))]));
                        curFilterThreaded.Add(true);
                        loopIdx += 2;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is", new NameExpr("None"))]));
                        curFilterThreaded.Add(true);
                        loopIdx += 2;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_FALSE)
                    {
                        curFilters.Add(new UnaryExpr("not", innerStack.Pop()));
                        curFilterThreaded.Add(true);
                        loopIdx += 2;
                        continue;
                    }
                }

                var targetOff = ResolveJumpTarget(cur.TargetOffset ?? 0);
                var isLoopEndOrContinue = targetOff >= endForOffset || targetOff == loopHeaderOffset;
                if (!isLoopEndOrContinue && _offsetToIndex.TryGetValue(targetOff, out var tIdx))
                {
                    isLoopEndOrContinue = _instrs[tIdx].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.END_FOR or OpCodes.FOR_ITER;
                }

                if (isLoopEndOrContinue)
                {
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_FALSE)
                    {
                        curFilters.Add(innerStack.Pop());
                        curFilterThreaded.Add(false);
                        loopIdx++;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_TRUE)
                    {
                        curFilters.Add(new UnaryExpr("not", innerStack.Pop()));
                        curFilterThreaded.Add(false);
                        loopIdx++;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is not", new NameExpr("None"))]));
                        curFilterThreaded.Add(false);
                        loopIdx++;
                        continue;
                    }
                    if (cur.OpCode == OpCodes.POP_JUMP_IF_NOT_NONE)
                    {
                        curFilters.Add(new CompareExpr(innerStack.Pop(), [("is", new NameExpr("None"))]));
                        curFilterThreaded.Add(false);
                        loopIdx++;
                        continue;
                    }
                }
            }

            if (TryDecompileConditional(loopIdx, limitIdx, innerStack, out var ifStmt, out var ifNextIdx))
            {
                loopIdx = ifNextIdx;
                continue;
            }

            if (cur.OpCode == OpCodes.YIELD_VALUE)
            {
                elt = innerStack.Pop();
                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, curAsync, curFilterThreaded));
                return;
            }

            if (cur.OpCode is OpCodes.LIST_APPEND or OpCodes.SET_ADD)
            {
                elt = innerStack.Pop();
                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, curAsync, curFilterThreaded));
                return;
            }

            if (cur.OpCode == OpCodes.MAP_ADD)
            {
                elt = innerStack.Pop();
                dictKey = innerStack.Pop();
                clauses.Add(new ComprehensionClause(curTarget, curIterator, curFilters, curAsync, curFilterThreaded));
                return;
            }

            ExecuteInstruction(cur, innerStack, null);
            loopIdx++;
        }
    }

    private static bool IsConditionExpressionOpcode(int op) => op is
        OpCodes.LOAD_CONST or OpCodes.LOAD_FAST or OpCodes.LOAD_FAST_CHECK or OpCodes.LOAD_NAME or
        OpCodes.LOAD_GLOBAL or OpCodes.LOAD_DEREF or OpCodes.LOAD_ATTR or
        OpCodes.COMPARE_OP or OpCodes.IS_OP or OpCodes.CONTAINS_OP or OpCodes.BINARY_OP or
        OpCodes.UNARY_NEGATIVE or OpCodes.UNARY_NOT or OpCodes.UNARY_INVERT or
        OpCodes.BUILD_TUPLE or OpCodes.BUILD_LIST or OpCodes.CALL or OpCodes.KW_NAMES or
        OpCodes.COPY or OpCodes.SWAP;

    private static bool SameConditionInstruction(PythonInstruction a, PythonInstruction b) =>
        a.OpCode == b.OpCode && Equals(a.ArgVal, b.ArgVal);

    private static IReadOnlyList<ExprNode> AttributeValues(ExprNode value)
    {
        if (value is TupleExpr tuple) return tuple.Elements;
        if (value is ConstantExpr { Value: System.Collections.IEnumerable seq and not string })
        {
            return seq.Cast<object?>().Select(x => (ExprNode)new ConstantExpr(x)).ToList();
        }
        return [value];
    }

    private static IReadOnlyList<string> ApplyFunctionDefaults(IReadOnlyList<string> parameters, ExprNode defaultsValue)
    {
        var defaults = AttributeValues(defaultsValue);
        var list = parameters.ToList();
        var positional = new List<int>();
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p == "/") continue;
            if (p == "*" || p.StartsWith("*")) break;
            positional.Add(i);
        }
        var apply = Math.Min(defaults.Count, positional.Count);
        for (var k = 0; k < apply; k++)
        {
            var idx = positional[positional.Count - apply + k];
            if (!list[idx].Contains('=')) list[idx] = $"{list[idx]}={PythonEmitter.EmitExpr(defaults[k])}";
        }
        return list;
    }

    private static IReadOnlyList<string> ApplyFunctionKwDefaults(IReadOnlyList<string> parameters, DictExpr dict)
    {
        var list = parameters.ToList();
        foreach (var (key, val) in dict.Entries)
        {
            var name = key is ConstantExpr { Value: string s } ? s : key is NameExpr n ? n.Name : null;
            if (string.IsNullOrEmpty(name)) continue;
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                var baseName = p.Split(':', '=')[0].Trim().TrimStart('*');
                if (baseName == name && !p.Contains('='))
                {
                    list[i] = $"{p}={PythonEmitter.EmitExpr(val)}";
                    break;
                }
            }
        }
        return list;
    }

    private static DictExpr? ExtractAnnotateDict(ExprNode annotate)
    {
        if (annotate is not FunctionExpr fn) return null;
        foreach (var stmt in fn.Body)
        {
            if (stmt is ReturnStmt { Value: DictExpr dict }) return dict;
        }
        return null;
    }

    private static ClassExpr BuildClassExprFromBuildClassCall(
        IReadOnlyList<ExprNode> posArgs, IReadOnlyList<(string Name, ExprNode Value)> kwArgs)
    {
        var classBodyFunc = posArgs[0];
        var classNameExpr = posArgs[1];
        var rawBases = posArgs.Skip(2).ToList();
        var bases = new List<ExprNode>();
        foreach (var b in rawBases)
        {
            if (b is GenericBaseExpr gbe)
            {
                if (gbe.TypeParams.Count > 0) {  }
            }
            else if (b is NameExpr { Name: ".generic_base" })
            {
            }
            else
            {
                bases.Add(b);
            }
        }
        var className = classNameExpr is ConstantExpr { Value: string nameStr } ? nameStr : "__pycrune_ph_class";
        List<StmtNode> bodyStmts = [];
        if (classBodyFunc is FunctionExpr fnBody)
        {
            bodyStmts = fnBody.Body
                .Where(s => s is not AssignStmt asgn || asgn.Targets.Count == 0 || asgn.Targets[0] is not NameExpr n || n.Name is not ("__module__" or "__qualname__" or "__classcell__" or "__type_params__" or "__firstlineno__" or "__static_attributes__"))
                .ToList();
            while (bodyStmts.Count > 0 && (
                bodyStmts[^1] is ReturnStmt ||
                (bodyStmts[^1] is AssignStmt asgnEnd && asgnEnd.Targets.Count > 0 && asgnEnd.Targets[0] is NameExpr nEnd && nEnd.Name is ("__module__" or "__qualname__" or "__classcell__" or "__type_params__" or "__firstlineno__" or "__static_attributes__")) ||
                (bodyStmts[^1] is ExprStmt exprEnd && exprEnd.Expression is NamedExpr neEnd && neEnd.Target is NameExpr netEnd && netEnd.Name == "__classcell__")
            ))
            {
                bodyStmts.RemoveAt(bodyStmts.Count - 1);
            }
        }
        return new ClassExpr(className, bases, kwArgs, bodyStmts, null, null);
    }

    private static FunctionExpr ApplyFunctionAnnotations(FunctionExpr fn, ExprNode annValue)
    {
        var pairs = new List<(string Name, ExprNode Value)>();
        if (annValue is TupleExpr tuple)
        {
            for (var i = 0; i + 1 < tuple.Elements.Count; i += 2)
            {
                if (tuple.Elements[i] is ConstantExpr { Value: string s }) pairs.Add((s, tuple.Elements[i + 1]));
            }
        }
        else if (annValue is DictExpr dict)
        {
            foreach (var (key, val) in dict.Entries)
            {
                var name = key is ConstantExpr { Value: string s } ? s : key is NameExpr n ? n.Name : null;
                if (name != null) pairs.Add((name, val));
            }
        }
        else if (annValue is ConstantExpr { Value: System.Collections.IEnumerable seq and not string })
        {
            var items = seq.Cast<object?>().ToList();
            for (var i = 0; i + 1 < items.Count; i += 2)
            {
                var name = items[i]?.ToString();
                if (name != null) pairs.Add((name, new ConstantExpr(items[i + 1])));
            }
        }
        if (pairs.Count == 0) return fn;

        var list = fn.Parameters.ToList();
        var returnType = fn.ReturnType;
        foreach (var (name, value) in pairs)
        {
            if (name == "return")
            {
                returnType = PythonEmitter.EmitExpr(value);
                continue;
            }
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                var baseName = p.Split(':', '=')[0].Trim().TrimStart('*');
                if (baseName == name && !p.Contains(':'))
                {
                    list[i] = $"{p}: {PythonEmitter.EmitExpr(value)}";
                    break;
                }
            }
        }
        return fn with { Parameters = list, ReturnType = returnType };
    }

    private bool TryParseTarget(ref int p, out ExprNode target)
    {
        target = null!;
        while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
        if (p >= _instrs.Count) return false;
        var ins = _instrs[p];

        if (ins.OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
        {
            var name = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "__pycrune_ph_x";
            target = new NameExpr(name);
            p++;
            return true;
        }

        if (ins.OpCode is OpCodes.UNPACK_SEQUENCE or OpCodes.UNPACK_EX)
        {
            return TryParseUnpackGroup(ref p, out target);
        }

        var startP = p;
        var targetStack = new SymbolicStack();
        try
        {
            while (p < _instrs.Count)
            {
                var cur = _instrs[p];
                if (cur.OpCode == OpCodes.STORE_ATTR)
                {
                    if (targetStack.Count > 0)
                    {
                        var obj = targetStack.Pop();
                        var attrName = cur.ArgRepr ?? cur.ArgVal?.ToString() ?? "__pycrune_ph_attr";
                        target = new AttributeExpr(obj, attrName);
                        p++;
                        return true;
                    }
                    break;
                }
                if (cur.OpCode == OpCodes.STORE_SUBSCR)
                {
                    if (targetStack.Count >= 2)
                    {
                        var key = targetStack.Pop();
                        var obj = targetStack.Pop();
                        target = new SubscriptExpr(obj, key);
                        p++;
                        return true;
                    }
                    break;
                }
                if (cur.OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF or OpCodes.UNPACK_SEQUENCE or OpCodes.UNPACK_EX)
                {
                    break;
                }
                ExecuteInstruction(cur, targetStack, null);
                p++;
            }
        }
        catch (Exception ex) when (ex is not FailFastException)
        {
            p = startP;
            return false;
        }

        p = startP;
        return false;
    }

    private bool TryDecompileSwapUnpackAssign(int idx, List<StmtNode> stmts, out int nextIdx)
    {
        nextIdx = idx;
        if (idx + 4 >= _instrs.Count) return false;
        if (_instrs[idx + 2].OpCode != OpCodes.SWAP || (_instrs[idx + 2].OpArg ?? 0) != 2) return false;
        if (_instrs[idx + 3].OpCode is not (OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)) return false;
        if (_instrs[idx + 4].OpCode is not (OpCodes.UNPACK_SEQUENCE or OpCodes.UNPACK_EX)) return false;

        var valueStack = new SymbolicStack();
        try
        {
            ExecuteInstruction(_instrs[idx], valueStack, null);
            ExecuteInstruction(_instrs[idx + 1], valueStack, null);
        }
        catch (Exception)
        {
            return false;
        }
        if (valueStack.Count != 2) return false;
        var v2 = valueStack.Pop();
        var v1 = valueStack.Pop();

        var p = idx + 3;
        try
        {
            if (!TryParseTarget(ref p, out var target1)) return false;
            if (p != idx + 4) return false;
            if (!TryParseUnpackGroup(ref p, out var target2)) return false;
            if (p <= idx + 4) return false;

            stmts.Add(new AssignStmt([new TupleExpr([target1, target2])], new TupleExpr([v1, v2])));
            nextIdx = p;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryParseUnpackGroup(ref int p, out ExprNode target)
    {
        target = null!;
        while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
        if (p >= _instrs.Count) return false;
        var ins = _instrs[p];

        if (ins.OpCode == OpCodes.UNPACK_SEQUENCE)
        {
            var count = ins.OpArg ?? 0;
            p++;
            var list = new List<ExprNode>(count);
            for (var i = 0; i < count; i++)
            {
                if (!TryParseTarget(ref p, out var sub)) return false;
                list.Add(sub);
            }
            target = new TupleExpr(list);
            return true;
        }

        if (ins.OpCode == OpCodes.UNPACK_EX)
        {
            var arg = ins.OpArg ?? 0;
            var before = arg & 0xFF;
            var after = (arg >> 8) & 0xFF;
            p++;
            var list = new List<ExprNode>(before + 1 + after);
            for (var i = 0; i < before; i++)
            {
                if (!TryParseTarget(ref p, out var sub)) return false;
                list.Add(sub);
            }
            if (!TryParseTarget(ref p, out var starSub)) return false;
            list.Add(new StarredExpr(starSub));
            for (var i = 0; i < after; i++)
            {
                if (!TryParseTarget(ref p, out var sub)) return false;
                list.Add(sub);
            }
            target = new TupleExpr(list);
            return true;
        }

        return false;
    }

    private bool TryDecompileUnpackAssign(int idx, SymbolicStack stack, List<StmtNode> stmts, out int nextIdx)
    {
        nextIdx = idx;
        if (idx >= _instrs.Count || stack.Count == 0) return false;
        var ins = _instrs[idx];
        if (ins.OpCode is not (OpCodes.UNPACK_SEQUENCE or OpCodes.UNPACK_EX)) return false;

        var p = idx;
        if (!TryParseUnpackGroup(ref p, out var target)) return false;

        var rhs = stack.Pop();
        stmts.Add(new AssignStmt([target], rhs));
        nextIdx = p;
        return true;
    }

    private bool TryDecompileMultiAssign(int idx, SymbolicStack stack, List<StmtNode> stmts, out int nextIdx)
    {
        nextIdx = idx;
        if (idx >= _instrs.Count || stack.Count < 2) return false;

        var k = 0;
        while (idx + k < _instrs.Count &&
               _instrs[idx + k].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
        {
            k++;
        }

        if (k < 2 || stack.Count < k) return false;

        var targets = new List<ExprNode>(k);
        for (var i = k - 1; i >= 0; i--)
        {
            var name = _instrs[idx + i].ArgRepr ?? _instrs[idx + i].ArgVal?.ToString() ?? $"v{i}";
            targets.Add(new NameExpr(name));
        }

        var values = stack.PopCount(k);
        stmts.Add(new AssignStmt([new TupleExpr(targets)], new TupleExpr(values)));
        nextIdx = idx + k;
        return true;
    }

    private bool TryDecompileInlinedFinallyReturn(int idx, SymbolicStack stack, List<StmtNode> stmts, out int nextIdx)
    {
        nextIdx = idx;
        if (_code.Name == "<module>" || idx + 1 >= _instrs.Count || stack.Count == 0) return false;

        var ins = _instrs[idx];
        if (ins.OpCode == OpCodes.SWAP && (ins.OpArg ?? 0) >= 2 && _instrs[idx + 1].OpCode == OpCodes.POP_TOP)
        {
            var p = idx + 2;
            while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
            while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.SWAP && (_instrs[p].OpArg ?? 0) >= 2 && p + 1 < _instrs.Count && _instrs[p + 1].OpCode == OpCodes.POP_TOP)
            {
                p += 2;
                while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
            }

            var scan = p;
            while (scan < _instrs.Count && scan < p + 30)
            {
                if (_instrs[scan].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                {
                    var retVal = stack.Pop();
                    stmts.Add(new ReturnStmt(retVal));
                    nextIdx = scan + 1;
                    return true;
                }
                scan++;
            }
        }
        return false;
    }

    private bool TryDecompileChainedAssign(int idx, SymbolicStack stack, List<StmtNode> stmts, out int nextIdx)
    {
        nextIdx = idx;
        if (idx >= _instrs.Count || stack.Count == 0) return false;


        if (_instrs[idx].OpCode == OpCodes.LEGACY_DUP_TOP)
        {
            var lp = idx + 1;
            var legacyTargets = new List<ExprNode>();
            while (lp < _instrs.Count &&
                   _instrs[lp].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
            {
                legacyTargets.Add(new NameExpr(_instrs[lp].ArgRepr ?? _instrs[lp].ArgVal?.ToString() ?? ""));
                lp++;
            }
            if (legacyTargets.Count >= 2)
            {
                var value = stack.Pop();
                stmts.Add(new AssignStmt(legacyTargets, value));
                nextIdx = lp;
                return true;
            }
            return false;
        }

        if (_instrs[idx].OpCode != OpCodes.COPY || (_instrs[idx].OpArg ?? 0) != 1) return false;

        var p = idx;
        var targets = new List<ExprNode>();

        while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.COPY && (_instrs[p].OpArg ?? 0) == 1)
        {
            var nextP = p + 1;
            if (!TryParseTarget(ref nextP, out var t)) break;
            targets.Add(t);
            p = nextP;
        }

        if (targets.Count == 0) return false;

        var finalP = p;
        if (!TryParseTarget(ref finalP, out var finalTarget)) return false;
        targets.Add(finalTarget);

        var rhs = stack.Pop();
        stmts.Add(new AssignStmt(targets, rhs));
        nextIdx = finalP;
        return true;
    }

    private static ExprNode ConvertStatementsToExpression(IReadOnlyList<StmtNode> stmts)
    {


        if (stmts.Any(s => s is ExprStmt { Expression: ConstantExpr }))
        {
            stmts = stmts.Where(s => s is not ExprStmt { Expression: ConstantExpr }).ToList();
        }

        if (stmts.Count == 1 && stmts[0] is ReturnStmt ret && ret.Value != null)
        {
            return ret.Value;
        }
        if (stmts.Count == 1 && stmts[0] is IfStmt ifStmt && ifStmt.ThenBody.Count > 0)
        {
            var ifTrue = ConvertStatementsToExpression(ifStmt.ThenBody);
            var ifFalse = ifStmt.ElseBody.Count > 0
                ? ConvertStatementsToExpression(ifStmt.ElseBody)
                : new ConstantExpr(null);
            return new TernaryExpr(ifStmt.Condition, ifTrue, ifFalse);
        }
        if (stmts.Count == 2 && stmts[0] is IfStmt ifNoElse && ifNoElse.ElseBody.Count == 0 && stmts[1] is ReturnStmt fallback)
        {
            var ifTrue = ConvertStatementsToExpression(ifNoElse.ThenBody);
            var ifFalse = fallback.Value ?? new ConstantExpr(null);
            return new TernaryExpr(ifNoElse.Condition, ifTrue, ifFalse);
        }
        if (stmts.Count > 0 && stmts[^1] is ReturnStmt lastRet && lastRet.Value != null)
        {
            return lastRet.Value;
        }


        if (stmts.Count == 0) return new ConstantExpr(null);
        throw new FailFastException("lambda body is not a single expression or return");
    }

    private List<string> BuildParameterList(PythonCodeObject co, List<ExprNode>? defaults, out string? returnAnnotation, Dictionary<string, ExprNode>? kwdefaults = null, Dictionary<string, ExprNode>? annotations = null)
    {
        returnAnnotation = null;
        if (annotations != null && annotations.TryGetValue("return", out var retAnn))
        {
            returnAnnotation = PythonEmitter.EmitExpr(retAnn);
        }

        var paramList = new List<string>();
        var posCount = co.ArgCount;
        var posOnly = co.PosOnlyArgCount;
        var kwOnly = co.KwOnlyArgCount;
        var defaultCount = defaults?.Count ?? 0;
        var firstDefaultIdx = posCount - defaultCount;

        for (var i = 0; i < posCount; i++)
        {
            var pName = i < co.VarNames.Count ? co.VarNames[i] : $"__pycrune_ph_arg{i}";
            var annotated = pName;
            ExprNode? annExpr = null;
            var hasAnn = annotations != null && annotations.TryGetValue(pName, out annExpr);
            if (hasAnn && annExpr != null)
            {
                annotated = $"{pName}: {PythonEmitter.EmitExpr(annExpr)}";
            }

            if (i >= firstDefaultIdx && defaults != null && i - firstDefaultIdx < defaults.Count)
            {
                var defExpr = defaults[i - firstDefaultIdx];
                var defStr = PythonEmitter.EmitExpr(defExpr);
                paramList.Add(hasAnn ? $"{annotated} = {defStr}" : $"{annotated}={defStr}");
            }
            else
            {
                paramList.Add(annotated);
            }
            if (posOnly > 0 && i == posOnly - 1)
            {
                paramList.Add("/");
            }
        }

        var varArgsIdx = posCount + kwOnly;
        var hasVarArgs = (co.Flags & 0x04) != 0 && varArgsIdx < co.VarNames.Count;
        if (hasVarArgs)
        {
            var vaName = co.VarNames[varArgsIdx];
            if (annotations != null && annotations.TryGetValue(vaName, out var vaAnn))
            {
                paramList.Add($"*{vaName}: {PythonEmitter.EmitExpr(vaAnn)}");
            }
            else
            {
                paramList.Add($"*{vaName}");
            }
        }
        else if (kwOnly > 0)
        {
            paramList.Add("*");
        }

        for (var k = 0; k < kwOnly; k++)
        {
            var pIdx = posCount + k;
            var kwName = pIdx < co.VarNames.Count ? co.VarNames[pIdx] : $"kw{k}";
            var annotatedKw = kwName;
            ExprNode? kwAnn = null;
            var hasKwAnn = annotations != null && annotations.TryGetValue(kwName, out kwAnn);
            if (hasKwAnn && kwAnn != null)
            {
                annotatedKw = $"{kwName}: {PythonEmitter.EmitExpr(kwAnn)}";
            }
            if (kwdefaults != null && kwdefaults.TryGetValue(kwName, out var kwDefVal))
            {
                var kwDefStr = PythonEmitter.EmitExpr(kwDefVal);
                paramList.Add(hasKwAnn ? $"{annotatedKw} = {kwDefStr}" : $"{annotatedKw}={kwDefStr}");
            }
            else
            {
                paramList.Add(annotatedKw);
            }
        }

        var kwArgsIdx = posCount + kwOnly + (hasVarArgs ? 1 : 0);
        if ((co.Flags & 0x08) != 0 && kwArgsIdx < co.VarNames.Count)
        {
            var kwaName = co.VarNames[kwArgsIdx];
            if (annotations != null && annotations.TryGetValue(kwaName, out var kwaAnn))
            {
                paramList.Add($"**{kwaName}: {PythonEmitter.EmitExpr(kwaAnn)}");
            }
            else
            {
                paramList.Add($"**{kwaName}");
            }
        }

        return paramList;
    }

    private void ExecuteInstruction(PythonInstruction ins, SymbolicStack stack, List<StmtNode>? stmts)
    {
        switch (ins.OpCode)
        {
            case OpCodes.PUSH_NULL:
                stack.Push(new NameExpr(""));
                break;

            case OpCodes.LEGACY_SETUP_FINALLY:
            case OpCodes.LEGACY_SETUP_WITH:


                throw new FailFastException(
                    $"Unrecognized legacy block '{ins.OpName}' at offset {ins.Offset}");

            case OpCodes.LEGACY_DUP_TOP:
                stack.Push(stack.Peek());
                break;

            case OpCodes.LEGACY_DUP_TOP_TWO:
            {
                var dupSecond = stack.Peek(1);
                var dupTop = stack.Peek();
                stack.Push(dupSecond);
                stack.Push(dupTop);
                break;
            }

            case OpCodes.LEGACY_ROT_TWO:
            {
                var rotTop = stack.Pop();
                var rotSecond = stack.Pop();
                stack.Push(rotTop);
                stack.Push(rotSecond);
                break;
            }

            case OpCodes.LEGACY_ROT_THREE:
            {
                var rotTop = stack.Pop();
                var rotSecond = stack.Pop();
                var rotThird = stack.Pop();
                stack.Push(rotTop);
                stack.Push(rotThird);
                stack.Push(rotSecond);
                break;
            }

            case OpCodes.LEGACY_ROT_FOUR:
            {
                var rotTop = stack.Pop();
                var rotSecond = stack.Pop();
                var rotThird = stack.Pop();
                var rotFourth = stack.Pop();
                stack.Push(rotTop);
                stack.Push(rotFourth);
                stack.Push(rotThird);
                stack.Push(rotSecond);
                break;
            }

            case OpCodes.LEGACY_ROT_N:
            {
                var rotN = ins.OpArg ?? 2;
                if (rotN <= 1 || stack.Count < rotN)
                {
                    throw new InvalidOperationException($"ROT_N {rotN} with stack of {stack.Count}");
                }
                var rotItems = new List<ExprNode>(rotN);
                for (var i = 0; i < rotN; i++) rotItems.Add(stack.Pop());
                stack.Push(rotItems[0]);
                for (var i = rotN - 1; i >= 1; i--) stack.Push(rotItems[i]);
                break;
            }

            case OpCodes.LEGACY_LIST_TO_TUPLE:
            {
                var listToTuple = stack.Pop();
                stack.Push(listToTuple is ListExpr listLiteral
                    ? new TupleExpr(listLiteral.Elements)
                    : new CallExpr(new NameExpr("tuple"), [listToTuple], []));
                break;
            }

            case OpCodes.LEGACY_BUILD_LIST_UNPACK:
            {
                var listUnpackCount = ins.OpArg ?? 0;
                var listUnpackItems = stack.PopCount(listUnpackCount);
                var listUnpackElems = new List<ExprNode>();
                foreach (var item in listUnpackItems)
                {


                    if (item is TupleExpr plainTuple) listUnpackElems.AddRange(plainTuple.Elements);
                    else listUnpackElems.Add(new StarredExpr(item));
                }
                stack.Push(new ListExpr(listUnpackElems));
                break;
            }

            case OpCodes.LEGACY_BUILD_TUPLE_UNPACK:
            case OpCodes.LEGACY_BUILD_TUPLE_UNPACK_WITH_CALL:
            {
                var tupleUnpackCount = ins.OpArg ?? 0;
                var tupleUnpackItems = stack.PopCount(tupleUnpackCount);
                var tupleElems = new List<ExprNode>();
                foreach (var item in tupleUnpackItems)
                {
                    if (item is TupleExpr plainTuple) tupleElems.AddRange(plainTuple.Elements);
                    else tupleElems.Add(new StarredExpr(item));
                }
                stack.Push(new TupleExpr(tupleElems));
                break;
            }

            case OpCodes.LEGACY_CALL_FINALLY:
            case OpCodes.LEGACY_COPY_DICT_WITHOUT_KEYS:
            case OpCodes.LEGACY_PREP_RERAISE_STAR:
            case OpCodes.LEGACY_ASYNC_GEN_WRAP:


                break;

            case OpCodes.LOAD_ASSERTION_ERROR:
                stack.Push(new AssertionErrorExpr());
                break;

            case OpCodes.LOAD_LOCALS:
                stack.Push(new NameExpr("locals()"));
                break;

            case OpCodes.LOAD_FROM_DICT_OR_DEREF or OpCodes.LOAD_FROM_DICT_OR_GLOBALS:
                var dictDerefName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                if (stack.Count > 0) stack.Pop();
                stack.Push(new NameExpr(dictDerefName));
                break;

            case OpCodes.LOAD_CONST:
                if (IsAtLeast314() && TryReconstructFoldedConstant(ins, out var foldedConst))
                {
                    stack.Push(foldedConst);
                }
                else if (ins.ArgVal is PycRune.Model.SliceValue sliceConst)
                {
                    stack.Push(new SliceExpr(
                        sliceConst.Start is null ? null : new ConstantExpr(sliceConst.Start),
                        sliceConst.Stop is null ? null : new ConstantExpr(sliceConst.Stop),
                        sliceConst.Step is null ? null : new ConstantExpr(sliceConst.Step)));
                }
                else if (ins.ArgVal is System.Collections.IEnumerable constSeq and not string &&
                         constSeq.Cast<object?>().Any(x => x is PycRune.Model.SliceValue))
                {


                    var elems = constSeq.Cast<object?>().Select(x => x is PycRune.Model.SliceValue sl
                        ? (ExprNode)new SliceExpr(
                            sl.Start is null ? null : new ConstantExpr(sl.Start),
                            sl.Stop is null ? null : new ConstantExpr(sl.Stop),
                            sl.Step is null ? null : new ConstantExpr(sl.Step))
                        : new ConstantExpr(x)).ToList();
                    stack.Push(new TupleExpr(elems));
                }
                else
                {
                    stack.Push(new ConstantExpr(ins.ArgVal));
                }
                break;

            case OpCodes.LOAD_NAME or OpCodes.LOAD_FAST or OpCodes.LOAD_FAST_CHECK or OpCodes.LOAD_GLOBAL or OpCodes.LOAD_DEREF:
                var name = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                if (name.StartsWith("NULL + ")) name = name[7..];
                else if (name.EndsWith(" + NULL")) name = name[..^7];
                stack.Push(new NameExpr(name));
                break;

            case OpCodes.LOAD_FAST_AND_CLEAR:
                var clearName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                if (clearName.StartsWith("NULL + ")) clearName = clearName[7..];
                else if (clearName.EndsWith(" + NULL")) clearName = clearName[..^7];
                if (!string.IsNullOrEmpty(clearName))
                {
                    _clearedFastVars.Add(clearName);
                }
                break;

            case OpCodes.LOAD_ATTR:
                var attrTarget = stack.Pop();
                var attrName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                if (attrName.StartsWith("NULL|self + ")) attrName = attrName[12..];
                else if (attrName.StartsWith("NULL + ")) attrName = attrName[7..];
                else if (attrName.EndsWith(" + NULL|self")) attrName = attrName[..^12];
                else if (attrName.EndsWith(" + NULL")) attrName = attrName[..^7];
                stack.Push(new AttributeExpr(attrTarget, attrName));
                break;

            case OpCodes.LOAD_SUPER_ATTR:
                if (stack.Count < 3)
                {
                    throw new InvalidOperationException("LOAD_SUPER_ATTR with stack underflow");
                }
                var superSelf = stack.Pop();
                var superClass = stack.Pop();
                var superGlobal = stack.Pop();
                var superAttrName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                var isSuperMethod = false;
                if (superAttrName.StartsWith("NULL|self + "))
                {
                    superAttrName = superAttrName[12..];
                    isSuperMethod = true;
                }
                else if (superAttrName.StartsWith("NULL + "))
                {
                    superAttrName = superAttrName[7..];
                    isSuperMethod = true;
                }
                else if (superAttrName.EndsWith(" + NULL|self"))
                {
                    superAttrName = superAttrName[..^12];
                    isSuperMethod = true;
                }
                else if (superAttrName.EndsWith(" + NULL"))
                {
                    superAttrName = superAttrName[..^7];
                    isSuperMethod = true;
                }
                if (isSuperMethod)
                {
                    stack.Push(new NameExpr(""));
                }
                stack.Push(new AttributeExpr(new CallExpr(new NameExpr("super"), [], []), superAttrName));
                break;

            case OpCodes.LOAD_BUILD_CLASS:
                stack.Push(new NameExpr("__build_class__"));
                break;

            case OpCodes.STORE_NAME or OpCodes.STORE_FAST or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF:
                var targetName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                if (stack.Count == 0 && _clearedFastVars.Contains(targetName) &&
                    _offsetToIndex.TryGetValue(ins.Offset, out var storeIdx) &&
                    storeIdx > 0 && _instrs[storeIdx - 1].OpCode == OpCodes.SWAP)
                {


                    break;
                }
                var val = stack.Pop();
                if (val is TypeAliasExpr ta)
                {
                    stmts?.Add(new TypeAliasStmt(ta.Name, ta.TypeParams, ta.Value));
                }
                else if (val is ClassExpr cls)
                {
                    stmts?.Add(new ClassDefStmt(cls.Name, cls.Bases, cls.Keywords, cls.Body, cls.Decorators, cls.TypeParams));
                }
                else if (val is FunctionExpr fn)
                {
                    stmts?.Add(new FunctionDefStmt(targetName, fn.Parameters, fn.Body, fn.Decorators, fn.IsAsync, fn.TypeParams, fn.ReturnType));
                }
                else if (val is BinaryExpr bAug && bAug.Op.EndsWith('='))
                {
                    stmts?.Add(new AugAssignStmt(new NameExpr(targetName), bAug.Op, bAug.Right));
                }
                else
                {
                    stmts?.Add(new AssignStmt([new NameExpr(targetName)], val));
                }
                break;

            case OpCodes.STORE_SUBSCR:
                var subKey = stack.Pop();
                var subObj = stack.Pop();
                var subVal = stack.Pop();
                if (subVal is BinaryExpr bSubAug && bSubAug.Op.EndsWith('='))
                {
                    stmts?.Add(new AugAssignStmt(new SubscriptExpr(subObj, subKey), bSubAug.Op, bSubAug.Right));
                }
                else
                {
                    stmts?.Add(new AssignStmt([new SubscriptExpr(subObj, subKey)], subVal));
                }
                break;

            case OpCodes.STORE_ATTR:
                var attrObj = stack.Pop();
                var attrVal = stack.Pop();
                var aName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                if (attrVal is BinaryExpr bAttrAug && bAttrAug.Op.EndsWith('='))
                {
                    stmts?.Add(new AugAssignStmt(new AttributeExpr(attrObj, aName), bAttrAug.Op, bAttrAug.Right));
                }
                else
                {
                    stmts?.Add(new AssignStmt([new AttributeExpr(attrObj, aName)], attrVal));
                }
                break;

            case OpCodes.DELETE_NAME or OpCodes.DELETE_FAST or OpCodes.DELETE_GLOBAL or OpCodes.DELETE_DEREF:
                var delName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                stmts?.Add(new DeleteStmt([new NameExpr(delName)]));
                break;

            case OpCodes.DELETE_SUBSCR:
                var delSubKey = stack.Pop();
                var delSubObj = stack.Pop();
                stmts?.Add(new DeleteStmt([new SubscriptExpr(delSubObj, delSubKey)]));
                break;

            case OpCodes.DELETE_ATTR:
                var delAttrObj = stack.Pop();
                var delAttrName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                stmts?.Add(new DeleteStmt([new AttributeExpr(delAttrObj, delAttrName)]));
                break;

            case OpCodes.POP_TOP:
                if (stack.Count > 0)
                {
                    var popped = stack.Pop();
                    if (popped is not NameExpr n || !string.IsNullOrEmpty(n.Name))
                    {
                        stmts?.Add(new ExprStmt(popped));
                    }
                }
                break;

            case OpCodes.UNARY_POSITIVE:
                stack.Push(new UnaryExpr("+", stack.Pop()));
                break;

            case OpCodes.UNARY_NEGATIVE:
                stack.Push(new UnaryExpr("-", stack.Pop()));
                break;

            case OpCodes.UNARY_NOT:
                stack.Push(new UnaryExpr("not", stack.Pop()));
                break;

            case OpCodes.UNARY_INVERT:
                stack.Push(new UnaryExpr("~", stack.Pop()));
                break;

            case OpCodes.BINARY_OP:
                var right = stack.Pop();
                var left = stack.Pop();

                if ((ins.OpArg ?? -1) == 26)
                {
                    stack.Push(new SubscriptExpr(left, right));
                    break;
                }
                var op = ins.ArgRepr ?? "+";
                stack.Push(new BinaryExpr(left, op, right));
                break;

            case OpCodes.UNPACK_SEQUENCE:
                var unpackCount = ins.OpArg ?? 0;
                var seqVal = stack.Pop();
                if (seqVal is TupleExpr teSeq && teSeq.Elements.Count == unpackCount)
                {
                    for (var i = unpackCount - 1; i >= 0; i--)
                    {
                        stack.Push(teSeq.Elements[i]);
                    }
                }
                else if (seqVal is ListExpr leSeq && leSeq.Elements.Count == unpackCount)
                {
                    for (var i = unpackCount - 1; i >= 0; i--)
                    {
                        stack.Push(leSeq.Elements[i]);
                    }
                }
                else if (seqVal is ConstantExpr { Value: System.Collections.IList listVal } && listVal.Count == unpackCount)
                {
                    for (var i = unpackCount - 1; i >= 0; i--)
                    {
                        stack.Push(new ConstantExpr(listVal[i]));
                    }
                }
                else
                {
                    if (unpackCount == 1)
                    {
                        stack.Push(new StarredExpr(seqVal));
                    }
                    else
                    {
                        for (var i = unpackCount - 1; i >= 0; i--)
                        {
                            stack.Push(new SubscriptExpr(seqVal, new ConstantExpr(i)));
                        }
                    }
                }
                break;

            case OpCodes.COMPARE_OP:
                var compRight = stack.Pop();
                var compLeft = stack.Pop();
                var compOp = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "==";
                stack.Push(new CompareExpr(compLeft, [(compOp, compRight)]));
                break;

            case OpCodes.IS_OP:
                var isRight = stack.Pop();
                var isLeft = stack.Pop();
                var isOp = (ins.OpArg ?? 0) == 1 ? "is not" : "is";
                stack.Push(new CompareExpr(isLeft, [(isOp, isRight)]));
                break;

            case OpCodes.CONTAINS_OP:
                var contRight = stack.Pop();
                var contLeft = stack.Pop();
                var contOp = (ins.OpArg ?? 0) == 1 ? "not in" : "in";
                stack.Push(new CompareExpr(contLeft, [(contOp, contRight)]));
                break;

            case OpCodes.BUILD_TUPLE:
                var tupCount = ins.OpArg ?? 0;
                var tupElems = stack.PopCount(tupCount);
                stack.Push(new TupleExpr(tupElems));
                break;

            case OpCodes.BUILD_LIST:
                var listCount = ins.OpArg ?? 0;
                var listElems = stack.PopCount(listCount);
                stack.Push(new ListExpr(listElems));
                break;

            case OpCodes.BUILD_SET:
                var setCount = ins.OpArg ?? 0;
                var setElems = stack.PopCount(setCount);
                stack.Push(new SetExpr(setElems));
                break;

            case OpCodes.BUILD_MAP:
                var mapCount = ins.OpArg ?? 0;
                var entries = new List<(ExprNode? Key, ExprNode Value)>();
                for (var i = 0; i < mapCount; i++)
                {
                    var mapVal = stack.Pop();
                    var mapKey = stack.Pop();
                    entries.Insert(0, (mapKey, mapVal));
                }
                stack.Push(new DictExpr(entries));
                break;

            case OpCodes.LEGACY_BUILD_MAP_UNPACK:
            case OpCodes.LEGACY_BUILD_MAP_UNPACK_WITH_CALL:
            {
                var unpCount = ins.OpArg ?? 0;
                var unpItems = stack.PopCount(unpCount);
                var unpEntries = new List<(ExprNode? Key, ExprNode Value)>();
                foreach (var item in unpItems)
                {


                    unpEntries.Add((null, item));
                }
                stack.Push(new DictExpr(unpEntries));
                break;
            }

            case OpCodes.LEGACY_BUILD_SET_UNPACK:
            {
                var setUnpackCount = ins.OpArg ?? 0;
                var setItems = stack.PopCount(setUnpackCount);
                stack.Push(new SetExpr(setItems.Select(i => i is SetExpr se
                    ? (ExprNode)new StarredExpr(se)
                    : new StarredExpr(i)).ToList()));
                break;
            }

            case OpCodes.LEGACY_CALL_FUNCTION_KW:
            {
                var kwTupleVal = stack.Pop();
                var kwNameList = new List<string>();
                if (kwTupleVal is TupleExpr kwTuple)
                {
                    foreach (var elem in kwTuple.Elements)
                    {
                        if (elem is ConstantExpr { Value: string s }) kwNameList.Add(s);
                    }
                }
                else if (kwTupleVal is ConstantExpr { Value: System.Collections.IEnumerable kwSeq and not string })
                {
                    foreach (var item in kwSeq)
                    {
                        if (item is string s) kwNameList.Add(s);
                    }
                }
                var kwArgc = ins.OpArg ?? 0;
                var kwAll = stack.PopCount(kwArgc);
                var kwPosCount = Math.Max(0, kwArgc - kwNameList.Count);
                var kwPosArgs = kwAll.Take(kwPosCount).ToList();
                var kwArgList = new List<(string Name, ExprNode Value)>();
                for (var i = 0; i < kwNameList.Count && kwPosCount + i < kwAll.Count; i++)
                {
                    kwArgList.Add((kwNameList[i], kwAll[kwPosCount + i]));
                }
                var kwFunc = stack.Pop();
                if (kwFunc is NameExpr { Name: "" } && stack.Count > 0)
                {
                    kwFunc = stack.Pop();
                }
                else if (stack.Count > 0 && stack.Peek() is NameExpr { Name: "" })
                {
                    stack.Pop();
                }
                if (kwFunc is NameExpr { Name: "__build_class__" } && kwPosArgs.Count >= 2)
                {
                    stack.Push(BuildClassExprFromBuildClassCall(kwPosArgs, kwArgList));
                    break;
                }
                stack.Push(new CallExpr(kwFunc, kwPosArgs, kwArgList));
                break;
            }

            case OpCodes.LEGACY_YIELD_FROM:
            {
                stack.Pop();
                var yieldIter = stack.Pop();
                stack.Push(new YieldFromExpr(yieldIter));
                break;
            }

            case OpCodes.KW_NAMES:
            {
                var kwnVal = ins.ArgVal;
                var kwList = new List<string>();
                if (kwnVal is System.Text.Json.JsonElement kwnJe && kwnJe.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var elem in kwnJe.EnumerateArray())
                    {
                        kwList.Add(elem.GetString() ?? elem.ToString());
                    }
                }
                else if (kwnVal is System.Collections.IEnumerable kwnEn and not string)
                {
                    foreach (var item in kwnEn)
                    {
                        if (item != null) kwList.Add(item.ToString()!);
                    }
                }
                else if (ins.ArgRepr is string kwnRepr && kwnRepr.StartsWith('(') && kwnRepr.EndsWith(')'))
                {
                    var inner = kwnRepr[1..^1].Trim();
                    var parts = inner.Split(',');
                    foreach (var p in parts)
                    {
                        var trimmed = p.Trim(' ', '\'', '"');
                        if (!string.IsNullOrEmpty(trimmed)) kwList.Add(trimmed);
                    }
                }
                _activeKwNames = kwList;
                break;
            }

            case OpCodes.CALL:
            {
                var argc = ins.OpArg ?? 0;
                var kwNames = _activeKwNames;
                _activeKwNames = null;
                var kwCount = kwNames?.Count ?? 0;
                if (argc == 0 && kwCount == 0 && stack.Count >= 2 && stack.Peek(1) is AssertionErrorExpr)
                {
                    var msgArg = stack.Pop();
                    var assertFunc = stack.Pop();
                    stack.Push(new CallExpr(assertFunc, [msgArg], []));
                    break;
                }
                var posCount = argc - kwCount;
                var allArgs = stack.PopCount(argc);
                var posArgs = allArgs.Take(posCount).ToList();
                var kwArgs = new List<(string Name, ExprNode Value)>();
                for (var i = 0; i < kwCount; i++)
                {
                    kwArgs.Add((kwNames![i], allArgs[posCount + i]));
                }
                var func = stack.Pop();
                if (func is NameExpr { Name: "" } && stack.Count > 0)
                {


                    func = stack.Pop();
                }
                else if (stack.Count > 0 && stack.Peek() is NameExpr nullName && string.IsNullOrEmpty(nullName.Name))
                {
                    stack.Pop();
                }
                if (func is FunctionExpr fnCallable && argc == 0)
                {
                    if (fnCallable.Name.StartsWith("<generic parameters of "))
                    {
                        var innerVal = fnCallable.Body.OfType<ReturnStmt>().FirstOrDefault()?.Value ?? fnCallable;
                        stack.Push(innerVal);
                        break;
                    }
                    if (stack.Count > 0)
                    {
                        var dec = stack.Pop();
                        var decs = fnCallable.Decorators != null ? new List<ExprNode>(fnCallable.Decorators) : new List<ExprNode>();
                        decs.Insert(0, dec);
                        stack.Push(new FunctionExpr(fnCallable.Name, fnCallable.Parameters, fnCallable.Body, decs, fnCallable.IsAsync, fnCallable.TypeParams, fnCallable.ReturnType));
                        break;
                    }
                }
                if (posArgs.Count == 1 && posArgs[0] is ClassExpr clsDec)
                {
                    var decs = clsDec.Decorators != null ? new List<ExprNode>(clsDec.Decorators) : new List<ExprNode>();
                    decs.Insert(0, func);
                    stack.Push(new ClassExpr(clsDec.Name, clsDec.Bases, clsDec.Keywords, clsDec.Body, decs, clsDec.TypeParams));
                    break;
                }
                if (func is NameExpr { Name: "__build_class__" } && posArgs.Count >= 2)
                {
                    var classBodyFunc = posArgs[0];
                    var classNameExpr = posArgs[1];
                    var rawBases = posArgs.Skip(2).ToList();
                    var bases = new List<ExprNode>();
                    List<string>? classTypeParams = _typeParams.Count > 0 ? new List<string>(_typeParams) : null;
                    foreach (var b in rawBases)
                    {
                        if (b is GenericBaseExpr gbe)
                        {
                            if (gbe.TypeParams.Count > 0) classTypeParams = new List<string>(gbe.TypeParams);
                        }
                        else if (b is NameExpr { Name: ".generic_base" })
                        {
                        }
                        else
                        {
                            bases.Add(b);
                        }
                    }
                    var className = classNameExpr is ConstantExpr { Value: string nameStr } ? nameStr : "__pycrune_ph_class";
                    List<StmtNode> bodyStmts = [];
                    if (classBodyFunc is FunctionExpr fnBody)
                    {
                        bodyStmts = fnBody.Body
                            .Where(s => s is not AssignStmt asgn || asgn.Targets.Count == 0 || asgn.Targets[0] is not NameExpr n || n.Name is not ("__module__" or "__qualname__" or "__classcell__" or "__type_params__" or "__firstlineno__" or "__static_attributes__"))
                            .ToList();
                        while (bodyStmts.Count > 0 && (
                            bodyStmts[^1] is ReturnStmt ||
                            (bodyStmts[^1] is AssignStmt asgnEnd && asgnEnd.Targets.Count > 0 && asgnEnd.Targets[0] is NameExpr nEnd && nEnd.Name is ("__module__" or "__qualname__" or "__classcell__" or "__type_params__" or "__firstlineno__" or "__static_attributes__")) ||
                            (bodyStmts[^1] is ExprStmt exprEnd && exprEnd.Expression is NamedExpr neEnd && neEnd.Target is NameExpr netEnd && netEnd.Name == "__classcell__")
                        ))
                        {
                            bodyStmts.RemoveAt(bodyStmts.Count - 1);
                        }
                    }
                    stack.Push(new ClassExpr(className, bases, kwArgs, bodyStmts, null, classTypeParams));
                    break;
                }
                if (func is ClassExpr clsCallable && argc == 0 && stack.Count > 0)
                {
                    var dec = stack.Pop();
                    var decs = clsCallable.Decorators != null ? new List<ExprNode>(clsCallable.Decorators) : new List<ExprNode>();
                    decs.Insert(0, dec);
                    stack.Push(new ClassExpr(clsCallable.Name, clsCallable.Bases, clsCallable.Keywords, clsCallable.Body, decs, clsCallable.TypeParams));
                    break;
                }


                if (argc == 1 && posArgs.Count == 1 && posArgs[0] is FunctionExpr decFn && kwArgs.Count == 0)
                {
                    var decs = decFn.Decorators != null ? new List<ExprNode>(decFn.Decorators) : new List<ExprNode>();
                    decs.Insert(0, func);
                    stack.Push(new FunctionExpr(decFn.Name, decFn.Parameters, decFn.Body, decs, decFn.IsAsync, decFn.TypeParams, decFn.ReturnType));
                    break;
                }
                var callInstrIdx = _offsetToIndex.TryGetValue(ins.Offset, out var curCallI) ? curCallI : -1;
                var nextInstrIdx = callInstrIdx + 1;
                while (nextInstrIdx >= 0 && nextInstrIdx < _instrs.Count &&
                       _instrs[nextInstrIdx].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN)
                {
                    nextInstrIdx++;
                }
                var nextOp = nextInstrIdx >= 0 && nextInstrIdx < _instrs.Count ? _instrs[nextInstrIdx].OpCode : -1;
                var nextArg = nextInstrIdx >= 0 && nextInstrIdx < _instrs.Count ? (_instrs[nextInstrIdx].OpArg ?? 0) : -1;


                var nextConsumesIterable = nextOp is OpCodes.GET_ITER or OpCodes.GET_AITER ||
                                           (nextOp == OpCodes.CALL && nextArg == 0);
                if (argc == 0 && !nextConsumesIterable && stack.Count > 0 &&
                    stack.Peek() is ConstantExpr { Value: PythonCodeObject compObj } && IsComprehensionCode(compObj))
                {


                    var outerIter = func;
                    func = stack.Pop();
                    posArgs.Insert(0, outerIter);
                }
                if (func is ConstantExpr { Value: PythonCodeObject compCode } && IsComprehensionCode(compCode))
                {
                    if (posArgs.Count == 0)
                    {
                        throw new FailFastException(
                            $"comprehension '{compCode.Name}' called without an outer iterator");
                    }
                    var compDecompiler = new StructuralDecompiler(compCode);
                    stack.Push(compDecompiler.DecompileLegacyComprehension(posArgs[0]));
                    break;
                }
                stack.Push(new CallExpr(func, posArgs, kwArgs));
                break;
            }

            case OpCodes.BINARY_SLICE:
                var sliceStop = stack.Pop();
                var sliceStart = stack.Pop();
                var sliceContainer = stack.Pop();
                var lower = sliceStart is ConstantExpr { Value: null } ? null : sliceStart;
                var upper = sliceStop is ConstantExpr { Value: null } ? null : sliceStop;
                stack.Push(new SubscriptExpr(sliceContainer, new SliceExpr(lower, upper, null)));
                break;

            case OpCodes.BUILD_SLICE:
                var sliceArgc = ins.OpArg ?? 2;
                ExprNode? step = sliceArgc == 3 ? stack.Pop() : null;
                var stopNode = stack.Pop();
                var startNode = stack.Pop();
                var sliceL = startNode is ConstantExpr { Value: null } ? null : startNode;
                var sliceU = stopNode is ConstantExpr { Value: null } ? null : stopNode;
                var sliceS = step is ConstantExpr { Value: null } ? null : step;
                stack.Push(new SliceExpr(sliceL, sliceU, sliceS));
                break;

            case OpCodes.STORE_SLICE:
                var storeSliceStop = stack.Pop();
                var storeSliceStart = stack.Pop();
                var storeSliceContainer = stack.Pop();
                var storeSliceVal = stack.Pop();
                var sLower = storeSliceStart is ConstantExpr { Value: null } ? null : storeSliceStart;
                var sUpper = storeSliceStop is ConstantExpr { Value: null } ? null : storeSliceStop;
                stmts?.Add(new AssignStmt([new SubscriptExpr(storeSliceContainer, new SliceExpr(sLower, sUpper, null))], storeSliceVal));
                break;

            case OpCodes.SET_UPDATE:
                var updateArg = stack.Pop();
                if (stack.Count > 0 && stack.Peek() is SetExpr setExpr)
                {
                    var merged = new List<ExprNode>(setExpr.Elements);
                    if (updateArg is ConstantExpr { Value: HashSet<object?> hashSet })
                    {


                        foreach (var item in hashSet)
                        {
                            merged.Add(new ConstantExpr(item));
                        }
                    }
                    else if (updateArg is ConstantExpr { Value: FrozenSetValue frozenSet })
                    {
                        foreach (var item in frozenSet.Items)
                        {
                            merged.Add(new ConstantExpr(item));
                        }
                    }
                    else
                    {


                        merged.Add(new StarredExpr(updateArg));
                    }
                    stack.Pop();
                    stack.Push(new SetExpr(merged));
                }
                else if (updateArg is ConstantExpr { Value: HashSet<object?> looseSet })
                {
                    stack.Push(new SetExpr(looseSet.Select(item => (ExprNode)new ConstantExpr(item)).ToList()));
                }
                else if (updateArg is ConstantExpr { Value: FrozenSetValue looseFrozen })
                {
                    stack.Push(new SetExpr(looseFrozen.Items.Select(item => (ExprNode)new ConstantExpr(item)).ToList()));
                }
                else
                {
                    stack.Push(new SetExpr([new StarredExpr(updateArg)]));
                }
                break;

            case OpCodes.LIST_APPEND:
                if (stack.Count == 0) break;
                var appendedVal = stack.Pop();
                var listDepth = (ins.OpArg ?? 1) - 1;
                if (listDepth >= 0 && listDepth < stack.Count && stack.Peek(listDepth) is ListExpr targetList)
                {
                    if (targetList.Elements is List<ExprNode> mList)
                    {
                        mList.Add(appendedVal);
                    }
                    else
                    {
                        var merged = new List<ExprNode>(targetList.Elements) { appendedVal };
                        stack.Pop();
                        stack.Push(new ListExpr(merged));
                    }
                }
                break;

            case OpCodes.SET_ADD:
                if (stack.Count == 0) break;
                var setVal = stack.Pop();
                var setDepth = (ins.OpArg ?? 1) - 1;
                if (setDepth >= 0 && setDepth < stack.Count && stack.Peek(setDepth) is SetExpr targetSet)
                {
                    if (targetSet.Elements is List<ExprNode> mSet)
                    {
                        mSet.Add(setVal);
                    }
                    else
                    {
                        var merged = new List<ExprNode>(targetSet.Elements) { setVal };
                        stack.Pop();
                        stack.Push(new SetExpr(merged));
                    }
                }
                break;

            case OpCodes.MAP_ADD:
                if (stack.Count < 2) break;
                var mapV = stack.Pop();
                var mapK = stack.Pop();
                var mapDepth = (ins.OpArg ?? 1) - 1;
                if (mapDepth >= 0 && mapDepth < stack.Count && stack.Peek(mapDepth) is DictExpr targetDict)
                {
                    if (targetDict.Entries is List<(ExprNode? Key, ExprNode Value)> mDict)
                    {
                        mDict.Add((mapK, mapV));
                    }
                    else
                    {
                        var merged = new List<(ExprNode? Key, ExprNode Value)>(targetDict.Entries) { (mapK, mapV) };
                        stack.Pop();
                        stack.Push(new DictExpr(merged));
                    }
                }
                break;

            case OpCodes.MATCH_SEQUENCE:
                stack.Push(new NameExpr("__pycrune_ph_match_subject"));
                break;

            case OpCodes.GET_LEN:
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException("GET_LEN with empty stack");
                }
                var lenTarget = stack.Peek();
                stack.Push(new CallExpr(new NameExpr("len"), [lenTarget], []));
                break;

            case OpCodes.MATCH_CLASS:
                if (stack.Count < 2)
                {
                    throw new InvalidOperationException("MATCH_CLASS with stack underflow");
                }
                var matchCount = ins.OpArg ?? 0;
                stack.Pop();
                stack.Pop();
                var matchArgs = Enumerable.Range(0, matchCount).Select(i => (ExprNode)new NameExpr($"__pycrune_ph_match_{i}")).ToList();
                stack.Push(new TupleExpr(matchArgs));
                break;

            case OpCodes.MATCH_MAPPING:
                stack.Push(new NameExpr("__pycrune_ph_match_subject"));
                break;

            case OpCodes.MATCH_KEYS:
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException("MATCH_KEYS with empty stack");
                }
                var keyTuple = stack.Pop();
                var kCount = 1;
                if (keyTuple is TupleExpr teKeys) kCount = teKeys.Elements.Count;
                else if (keyTuple is ConstantExpr { Value: System.Collections.ICollection col }) kCount = col.Count;
                var keyVals = Enumerable.Range(0, kCount).Select(i => (ExprNode)new NameExpr($"__pycrune_ph_val_{i}")).ToList();
                stack.Push(new TupleExpr(keyVals));
                break;

            case OpCodes.UNPACK_EX:
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException("UNPACK_EX with empty stack");
                }
                var exArg = ins.OpArg ?? 0;
                var exBefore = exArg & 0xFF;
                var exAfter = (exArg >> 8) & 0xFF;
                var exSeq = stack.Pop();
                var total = exBefore + 1 + exAfter;
                for (var i = total - 1; i >= 0; i--)
                {
                    stack.Push(new SubscriptExpr(exSeq, new ConstantExpr(i)));
                }
                break;

            case OpCodes.CALL_INTRINSIC_1:
                var intrin1 = ins.OpArg ?? 0;
                if (intrin1 == 5 && stack.Count > 0)
                {

                    stack.Push(new UnaryExpr("+", stack.Pop()));
                }
                else if (intrin1 == 6 && stack.Count > 0)
                {
                    var listTop = stack.Pop();
                    if (listTop is ListExpr listExprTop)
                    {
                        stack.Push(new TupleExpr(listExprTop.Elements));
                    }
                    else
                    {
                        stack.Push(listTop);
                    }
                }
                else if ((intrin1 is 7 or 8 or 9) && stack.Count > 0)
                {
                    var tName = stack.Pop();
                    var tpStr = tName is ConstantExpr { Value: string sVal } ? sVal : (tName is NameExpr ne ? ne.Name : tName.ToString());
                    if (intrin1 == 8) tpStr = $"**{tpStr}";
                    else if (intrin1 == 9) tpStr = $"*{tpStr}";
                    if (!string.IsNullOrEmpty(tpStr) && !_typeParams.Contains(tpStr))
                    {
                        _typeParams.Add(tpStr);
                    }
                    stack.Push(tName);
                }
                else if (intrin1 == 10 && stack.Count > 0)
                {
                    var tpArg = stack.Pop();
                    var tpList = new List<string>(_typeParams);
                    if (tpArg is TupleExpr teGen)
                    {
                        var extracted = teGen.Elements
                            .Select(e => e is NameExpr n ? n.Name : (e is ConstantExpr c ? c.Value?.ToString() ?? "" : ""))
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                        if (extracted.Count > 0) tpList = extracted;
                    }
                    stack.Push(new GenericBaseExpr(tpList));
                }
                else if (intrin1 == 11 && stack.Count > 0)
                {
                    var aliasTuple = stack.Pop();
                    string? aliasName = null;
                    var typeParams = new List<string>();
                    ExprNode? aliasValue = null;
                    if (aliasTuple is TupleExpr teAlias && teAlias.Elements.Count == 3)
                    {
                        if (teAlias.Elements[0] is ConstantExpr { Value: string nameStr }) aliasName = nameStr;
                        if (teAlias.Elements[1] is TupleExpr tpTuple)
                        {
                            typeParams = tpTuple.Elements
                                .Select(e => {
                                    var s = e is NameExpr n ? n.Name : (e is ConstantExpr c ? c.Value?.ToString() ?? "" : "");
                                    var matched = _typeParams.FirstOrDefault(tp => tp == s || tp == $"**{s}" || tp == $"*{s}" || tp.StartsWith($"{s}:") || tp.StartsWith($"{s} "));
                                    return matched ?? s;
                                })
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList();
                        }
                        if (teAlias.Elements[2] is FunctionExpr fnVal)
                        {
                            aliasValue = fnVal.Body.OfType<ReturnStmt>().FirstOrDefault()?.Value;
                        }
                    }
                    if (aliasName == null || aliasValue == null)
                        throw new FailFastException("malformed PEP 695 type alias intrinsic");
                    stack.Push(new TypeAliasExpr(aliasName, typeParams, aliasValue));
                }
                break;

            case OpCodes.CALL_INTRINSIC_2:
                var intrin2 = ins.OpArg ?? 0;
                if ((intrin2 == 2 || intrin2 == 3) && stack.Count >= 2)
                {
                    var boundTop = stack.Pop();
                    var nameTop = stack.Pop();
                    var tpName = nameTop is ConstantExpr { Value: string sVal } ? sVal : (nameTop is NameExpr ne ? ne.Name : nameTop.ToString());
                    ExprNode? boundExpr = null;
                    if (boundTop is FunctionExpr feBound)
                    {
                        boundExpr = feBound.Body.OfType<ReturnStmt>().FirstOrDefault()?.Value;
                    }
                    else
                    {
                        boundExpr = boundTop;
                    }

                    var tpStr = boundExpr != null
                        ? $"{tpName}: {PythonEmitter.EmitExpr(boundExpr)}"
                        : tpName;

                    if (!string.IsNullOrEmpty(tpStr) && !_typeParams.Contains(tpStr))
                    {
                        _typeParams.Add(tpStr);
                    }
                    stack.Push(nameTop);
                }
                else if (intrin2 == 4 && stack.Count >= 2)
                {
                    var firstTop = stack.Pop();
                    var secondTop = stack.Pop();
                    var fe = firstTop as FunctionExpr ?? secondTop as FunctionExpr;
                    var teTp = firstTop as TupleExpr ?? secondTop as TupleExpr;
                    var tpList = new List<string>();
                    if (teTp != null)
                    {
                        tpList = teTp.Elements
                            .Select(e => {
                                var s = e is NameExpr n ? n.Name : (e is ConstantExpr c ? c.Value?.ToString() ?? "" : e.ToString());
                                var matched = _typeParams.FirstOrDefault(tp => tp == s || tp == $"**{s}" || tp == $"*{s}" || tp.StartsWith($"{s}:") || tp.StartsWith($"{s} "));
                                return matched ?? s;
                            })
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                    }
                    if (fe != null)
                    {
                        stack.Push(new FunctionExpr(fe.Name, fe.Parameters, fe.Body, fe.Decorators, fe.IsAsync, tpList, fe.ReturnType));
                    }
                    else
                    {
                        stack.Push(firstTop);
                    }
                }
                break;

            case OpCodes.RETURN_GENERATOR or OpCodes.RESUME or OpCodes.END_FOR:
                break;

            case OpCodes.SWAP:
                var swapN = (ins.OpArg ?? 2) - 1;
                if (stack.Count > swapN)
                {
                    stack.Swap(swapN);
                }
                break;

            case OpCodes.COPY:
                var copyN = ins.OpArg ?? 1;
                if (stack.Count >= copyN)
                {
                    stack.Copy(copyN);
                }
                break;

            case OpCodes.POP_EXCEPT:
                break;

            case OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE:
                if (stack.Count > 0)
                {
                    stack.Pop();
                }
                break;

            case OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT:
                var targetOffset = ins.TargetOffset ?? -1;
                if (_activeLoopExits.Contains(targetOffset))
                {
                    stmts?.Add(new BreakStmt());
                }
                else if (IsActiveLoopHeader(targetOffset))
                {
                    stmts?.Add(new ContinueStmt());
                }
                break;

            case OpCodes.RETURN_CONST:
                var constRetVal = new ConstantExpr(ins.ArgVal);
                if (_code.Name != "<module>" && (constRetVal.Value != null || (_offsetToIndex.TryGetValue(ins.Offset, out var rIdx) && rIdx < _instrs.Count - 1)))
                {
                    stmts?.Add(new ReturnStmt(constRetVal) { FromReturnConst = true });
                }
                break;

            case OpCodes.RETURN_VALUE:
                if (stack.Count > 0)
                {
                    var retVal = stack.Pop();


                    var isLast = _offsetToIndex.TryGetValue(ins.Offset, out var rIdx2) && rIdx2 == _instrs.Count - 1;
                    var operandIsJumpTarget = rIdx2 > 0 && _instrs[rIdx2 - 1].IsJumpTarget;
                    var isTrailing = isLast || IsFollowedOnlyByHandlerCode(ins);
                    var mustKeep = retVal is not ConstantExpr c || c.Value != null || !isTrailing ||
                                   (isLast ? operandIsJumpTarget : IsTargetedByConditionalJump(ins.Offset));
                    if (_code.Name != "<module>" && mustKeep)
                    {
                        stmts?.Add(new ReturnStmt(retVal));
                    }
                }
                break;

            case OpCodes.RAISE_VARARGS:
                var raiseArgc = ins.OpArg ?? 0;
                ExprNode? exc = null;
                ExprNode? cause = null;
                if (raiseArgc == 2)
                {
                    cause = stack.Pop();
                    exc = stack.Pop();
                }
                else if (raiseArgc == 1)
                {
                    exc = stack.Pop();
                }
                if (TryBuildAssertFromRaise(exc, stmts))
                {
                    break;
                }
                stmts?.Add(new RaiseStmt(exc, cause));
                break;

            case OpCodes.RERAISE:
                break;

            case OpCodes.GET_ITER:
                break;

            case OpCodes.GET_AITER:
                break;

            case OpCodes.GET_ANEXT:
                stack.Push(new NameExpr("anext"));
                break;

            case OpCodes.END_ASYNC_FOR:
                break;

            case OpCodes.CLEANUP_THROW:
                break;

            case OpCodes.BUILD_CONST_KEY_MAP:
                var bckCount = ins.OpArg ?? 0;
                var keysTuple = stack.Pop();
                var bckVals = stack.PopCount(bckCount);
                var bckEntries = new List<(ExprNode? Key, ExprNode Value)>();
                if (keysTuple is TupleExpr te && te.Elements.Count == bckCount)
                {
                    for (var i = 0; i < bckCount; i++)
                    {
                        bckEntries.Add((te.Elements[i], bckVals[i]));
                    }
                }
                else if (keysTuple is ConstantExpr { Value: System.Collections.IEnumerable list })
                {
                    var i = 0;
                    foreach (var k in list)
                    {
                        if (i < bckVals.Count)
                        {
                            bckEntries.Add((new ConstantExpr(k), bckVals[i++]));
                        }
                    }
                }
                stack.Push(new DictExpr(bckEntries));
                break;

            case OpCodes.CONVERT_VALUE:
            {
                var convVal = stack.Pop();
                var conv = (ins.OpArg ?? 0) switch
                {
                    1 => "s",
                    2 => "r",
                    3 => "a",
                    _ => null
                };
                stack.Push(new FormattedValueExpr(convVal, conv, null));
                break;
            }

            case OpCodes.FORMAT_SIMPLE:
            {
                var simpleVal = stack.Pop();


                stack.Push(simpleVal is FormattedValueExpr { Conversion: null } nestedFv
                    ? new FormattedValueExpr(new JoinedStrExpr([nestedFv]))
                    : simpleVal is FormattedValueExpr sfv
                        ? sfv
                        : new FormattedValueExpr(simpleVal));
                break;
            }

            case OpCodes.FORMAT_WITH_SPEC:
            {
                var specVal = stack.Pop();
                var fmtVal = stack.Pop();
                stack.Push(fmtVal is FormattedValueExpr wfv
                    ? wfv with { FormatSpec = specVal }
                    : new FormattedValueExpr(fmtVal, null, specVal));
                break;
            }

            case OpCodes.SET_FUNCTION_ATTRIBUTE:
            {
                var attrFn = stack.Pop();
                var attrValue = stack.Pop();
                if (attrFn is FunctionExpr fnExpr)
                {
                    var kind = ins.OpArg ?? 0;
                    if (kind == 1)
                    {
                        fnExpr = fnExpr with { Parameters = ApplyFunctionDefaults(fnExpr.Parameters, attrValue) };
                    }
                    else if (kind == 2 && attrValue is DictExpr kwDict)
                    {
                        fnExpr = fnExpr with { Parameters = ApplyFunctionKwDefaults(fnExpr.Parameters, kwDict) };
                    }
                    else if (kind == 4)
                    {
                        fnExpr = ApplyFunctionAnnotations(fnExpr, attrValue);
                    }
                    else if (kind == 16)
                    {


                        var annDict = ExtractAnnotateDict(attrValue);
                        if (annDict != null)
                        {
                            fnExpr = ApplyFunctionAnnotations(fnExpr, annDict);
                        }
                    }
                    stack.Push(fnExpr);
                }
                else if (attrFn is LambdaExpr lam)
                {
                    var kind = ins.OpArg ?? 0;
                    if (kind == 1)
                    {
                        lam = lam with { Parameters = ApplyFunctionDefaults(lam.Parameters, attrValue) };
                    }
                    else if (kind == 2 && attrValue is DictExpr lamKw)
                    {
                        lam = lam with { Parameters = ApplyFunctionKwDefaults(lam.Parameters, lamKw) };
                    }
                    stack.Push(lam);
                }
                else
                {
                    stack.Push(attrFn);
                }
                break;
            }

            case OpCodes.FORMAT_VALUE:
                var fvFlags = ins.OpArg ?? 0;
                ExprNode? fvFmtSpec = null;
                if ((fvFlags & 0x04) != 0)
                {
                    if (stack.Count == 0)
                    {
                        throw new InvalidOperationException("FORMAT_VALUE format spec missing");
                    }
                    fvFmtSpec = stack.Pop();
                }
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException("FORMAT_VALUE with empty stack");
                }
                var fvVal = stack.Pop();
                var fvConv = (fvFlags & 0x03) switch
                {
                    1 => "s",
                    2 => "r",
                    3 => "a",
                    _ => null
                };
                stack.Push(new FormattedValueExpr(fvVal, fvConv, fvFmtSpec));
                break;

            case OpCodes.BUILD_STRING:
                var strCount = ins.OpArg ?? 0;
                var strParts = stack.PopCount(strCount);
                if (strParts.All(p => p is ConstantExpr))
                {
                    var combined = string.Concat(strParts.Select(p => ((ConstantExpr)p).Value?.ToString() ?? ""));
                    stack.Push(new ConstantExpr(combined));
                }
                else
                {
                    stack.Push(new JoinedStrExpr(strParts));
                }
                break;

            case OpCodes.MAKE_FUNCTION:
                var codeVal = stack.Pop();


                if (codeVal is ConstantExpr { Value: string qname } && !qname.StartsWith("<code:", StringComparison.Ordinal) &&
                    stack.Count > 0 && stack.Peek() is ConstantExpr { Value: string candidateCodeStr } && candidateCodeStr.StartsWith("<code:", StringComparison.Ordinal))
                {
                    codeVal = stack.Pop();
                }
                var flags = ins.OpArg ?? 0;
                if ((flags & 0x08) != 0 && stack.Count > 0) stack.Pop();
                Dictionary<string, ExprNode>? annotations = null;
                if ((flags & 0x04) != 0 && stack.Count > 0)
                {
                    var annVal = stack.Pop();
                    if (annVal is TupleExpr annTuple)
                    {
                        annotations = new Dictionary<string, ExprNode>();
                        for (var ai = 0; ai + 1 < annTuple.Elements.Count; ai += 2)
                        {
                            var keyExpr = annTuple.Elements[ai];
                            var valExpr = annTuple.Elements[ai + 1];
                            var key = keyExpr is ConstantExpr { Value: string kStr } ? kStr : (keyExpr is NameExpr kn ? kn.Name : keyExpr.ToString());
                            if (!string.IsNullOrEmpty(key))
                            {
                                annotations[key] = valExpr;
                            }
                        }
                    }
                    else if (annVal is DictExpr annDict)
                    {
                        annotations = new Dictionary<string, ExprNode>();
                        foreach (var (k, v) in annDict.Entries)
                        {
                            var key = k is ConstantExpr { Value: string kStr } ? kStr : (k is NameExpr kn ? kn.Name : k?.ToString());
                            if (!string.IsNullOrEmpty(key))
                            {
                                annotations[key] = v;
                            }
                        }
                    }
                    else if (annVal is ConstantExpr { Value: System.Collections.IEnumerable annSeq and not string })
                    {


                        annotations = new Dictionary<string, ExprNode>();
                        var annItems = annSeq.Cast<object?>().ToList();
                        for (var ai = 0; ai + 1 < annItems.Count; ai += 2)
                        {
                            var key = annItems[ai]?.ToString();
                            if (!string.IsNullOrEmpty(key))
                            {
                                annotations[key] = new ConstantExpr(annItems[ai + 1]);
                            }
                        }
                    }
                }
                Dictionary<string, ExprNode>? kwdefaults = null;
                if ((flags & 0x02) != 0 && stack.Count > 0)
                {
                    var kwdVal = stack.Pop();
                    if (kwdVal is DictExpr de)
                    {
                        kwdefaults = new Dictionary<string, ExprNode>();
                        foreach (var (k, v) in de.Entries)
                        {
                            if (k is ConstantExpr { Value: string kStr }) kwdefaults[kStr] = v;
                            else if (k is NameExpr n) kwdefaults[n.Name] = v;
                        }
                    }
                }
                List<ExprNode>? defaults = null;
                if ((flags & 0x01) != 0 && stack.Count > 0)
                {
                    var dVal = stack.Pop();
                    if (dVal is TupleExpr teDef)
                    {
                        defaults = teDef.Elements.ToList();
                    }
                    else if (dVal is ConstantExpr { Value: object[] arr })
                    {
                        defaults = arr.Select(x => (ExprNode)new ConstantExpr(x)).ToList();
                    }
                    else if (dVal is ConstantExpr { Value: System.Text.Json.JsonElement je } && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        defaults = je.EnumerateArray().Select(e => (ExprNode)new ConstantExpr(e.ValueKind switch {
                            System.Text.Json.JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
                            System.Text.Json.JsonValueKind.String => e.GetString()!,
                            System.Text.Json.JsonValueKind.True => true,
                            System.Text.Json.JsonValueKind.False => false,
                            _ => e.ToString()
                        })).ToList();
                    }
                    else if (dVal is ConstantExpr { Value: System.Collections.IEnumerable list and not string })
                    {
                        defaults = ((System.Collections.IEnumerable)list).Cast<object>().Select(x => (ExprNode)new ConstantExpr(x)).ToList();
                    }
                }
                var funcName = "__pycrune_ph_func";
                PythonCodeObject? nestedCode = null;
                if (codeVal is ConstantExpr { Value: string codeStr } && codeStr.StartsWith("<code:"))
                {
                    var inner = codeStr[6..^1];
                    var parts = inner.Split(':');
                    funcName = parts[0];
                    if (parts.Length > 1 && int.TryParse(parts[1], out var nestedIdx) && nestedIdx < _code.NestedCodes.Count)
                    {
                        nestedCode = _code.NestedCodes[nestedIdx];
                    }
                }
                if (nestedCode == null && codeVal is ConstantExpr { Value: string codeName } &&
                    codeName.StartsWith("<code:", StringComparison.Ordinal))
                {
                    PythonCodeObject? unique = null;
                    foreach (var candidate in _code.NestedCodes)
                    {
                        if (candidate.Name != funcName) continue;
                        if (unique != null)
                        {
                            throw new FailFastException(
                                $"ambiguous nested code object '{funcName}' in '{_code.Name}'");
                        }
                        unique = candidate;
                    }
                    nestedCode = unique ??
                        throw new FailFastException(
                            $"nested code object '{funcName}' not found in '{_code.Name}'");
                }
                if (nestedCode != null)
                {
                    if (funcName is "<genexpr>" or "<listcomp>" or "<setcomp>" or "<dictcomp>")
                    {
                        stack.Push(new ConstantExpr(nestedCode));
                        break;
                    }

                    var decompiler = new StructuralDecompiler(nestedCode);
                    var funcStmts = decompiler.Decompile();
                    var parameters = BuildParameterList(nestedCode, defaults, out var returnAnnotation, kwdefaults, annotations);
                    var isAsync = (nestedCode.Flags & 0x80) != 0 || (nestedCode.Flags & 0x200) != 0;
                    if (funcName == "<lambda>")
                    {
                        ExprNode lambdaBody = ConvertStatementsToExpression(funcStmts);
                        stack.Push(new LambdaExpr(parameters, lambdaBody));
                    }
                    else
                    {
                        var isGenerator = (nestedCode.Flags & 0x20) != 0 || (nestedCode.Flags & 0x200) != 0;
                        if (funcStmts.Count > 0 && funcStmts[^1] is ReturnStmt { Value: null or ConstantExpr { Value: null } } tailRet &&
                            (tailRet.FromReturnConst || isGenerator))
                        {


                            funcStmts.RemoveAt(funcStmts.Count - 1);
                        }
                        stack.Push(new FunctionExpr(nestedCode.Name, parameters, funcStmts, null, isAsync, null, returnAnnotation));
                    }
                }
                else
                {
                    stack.Push(codeVal);
                }
                break;

            case OpCodes.DICT_MERGE:
                if (stack.Count >= 2)
                {
                    var mergeSource = stack.Pop();
                    var mergeTarget = stack.Pop();
                    if (mergeTarget is DictExpr dt)
                    {
                        var newEntries = new List<(ExprNode? Key, ExprNode Value)>(dt.Entries);


                        newEntries.Add((null, mergeSource));
                        stack.Push(new DictExpr(newEntries));
                    }
                    else
                    {
                        stack.Push(new DictExpr([(null, mergeTarget), (null, mergeSource)]));
                    }
                }
                break;

            case OpCodes.CALL_FUNCTION_EX:
                var hasKw = ((ins.OpArg ?? 0) & 1) != 0;
                ExprNode? kwVal = null;
                if (IsAtLeast314())
                {


                    if (stack.Count > 0)
                    {
                        var kwSlot = stack.Pop();
                        if (kwSlot is not NameExpr { Name: "" }) kwVal = kwSlot;
                    }
                }
                else if (hasKw)
                {
                    if (stack.Count == 0)
                    {
                        throw new InvalidOperationException("CALL_FUNCTION_EX keyword mapping missing");
                    }
                    kwVal = stack.Pop();
                }
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException("CALL_FUNCTION_EX positional tuple missing");
                }
                ExprNode? varargsVal = stack.Pop();
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException("CALL_FUNCTION_EX callable missing");
                }
                ExprNode callable = stack.Pop();
                if (callable is NameExpr { Name: "" } && stack.Count > 0)
                {

                    callable = stack.Pop();
                }
                else if (stack.Count > 0 && stack.Peek() is NameExpr nullNameEx && string.IsNullOrEmpty(nullNameEx.Name))
                {
                    stack.Pop();
                }
                var callArgs = new List<ExprNode>();
                var callKwargs = new List<(string Name, ExprNode Value)>();
                List<(string? Name, ExprNode Value)>? kwSequence = null;
                if (varargsVal is TupleExpr teArgs)
                {
                    callArgs.AddRange(teArgs.Elements);
                }
                else if (varargsVal is ListExpr leArgs)
                {


                    callArgs.Add(new StarredExpr(leArgs));
                }
                else if (varargsVal is ConstantExpr { Value: System.Collections.IList { Count: 0 } } emptyArgs)
                {


                    var emptyTupleIsLastConst = _code.Constants.Count > 0 &&
                        _code.Constants[^1] is System.Collections.IList { Count: 0 };
                    if (!emptyTupleIsLastConst)
                    {
                        callArgs.Add(new StarredExpr(emptyArgs));
                    }
                }
                else if (varargsVal != null)
                {
                    callArgs.Add(new StarredExpr(varargsVal));
                }

                if (kwVal is DictExpr deKw && !deKw.Entries.Any(e => e.Key == null))
                {


                    callArgs.Add(new DoubleStarredExpr(deKw));
                }
                else if (kwVal is DictExpr deKwMerged)
                {


                    kwSequence = [];
                    foreach (var (k, v) in deKwMerged.Entries)
                    {
                        if (k is ConstantExpr { Value: string kStr })
                        {
                            callKwargs.Add((kStr, v));
                            kwSequence.Add((kStr, v));
                        }
                        else if (k is NameExpr kn)
                        {
                            callKwargs.Add((kn.Name, v));
                            kwSequence.Add((kn.Name, v));
                        }
                        else if (k == null)
                        {
                            kwSequence.Add((null, v));
                        }
                        else
                        {
                            callKwargs.Add((k.ToString(), v));
                            kwSequence.Add((k.ToString(), v));
                        }
                    }
                }
                else if (kwVal != null)
                {
                    callArgs.Add(new DoubleStarredExpr(kwVal));
                }

                stack.Push(new CallExpr(callable, callArgs, callKwargs, kwSequence));
                break;

            case OpCodes.BINARY_SUBSCR:
                var subIndex = stack.Pop();
                var subTarget = stack.Pop();
                stack.Push(new SubscriptExpr(subTarget, subIndex));
                break;

            case OpCodes.LIST_EXTEND:
                var extendItems = stack.Pop();
                var baseList = stack.Pop();
                if (baseList is ListExpr le)
                {
                    var elements = new List<ExprNode>(le.Elements);
                    if (extendItems is ConstantExpr { Value: System.Collections.IEnumerable col and not string } && elements.Count == 0)
                    {


                        foreach (var item in col) elements.Add(new ConstantExpr(item));
                    }
                    else
                    {


                        elements.Add(new StarredExpr(extendItems));
                    }
                    stack.Push(new ListExpr(elements));
                }
                else
                {
                    stack.Push(baseList);
                }
                break;

            case OpCodes.DICT_UPDATE or OpCodes.DICT_MERGE:
            {
                var updateItems = stack.Pop();
                var baseDict = stack.Pop();
                if (baseDict is DictExpr targetDictExpr)
                {
                    var duEntries = new List<(ExprNode? Key, ExprNode Value)>(targetDictExpr.Entries);


                    duEntries.Add((null, updateItems));
                    stack.Push(new DictExpr(duEntries));
                }
                else
                {
                    stack.Push(baseDict);
                }
                break;
            }

            case OpCodes.LOAD_CLOSURE:
                var closureName = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
                stack.Push(new NameExpr(closureName));
                break;

            case OpCodes.YIELD_VALUE:
                var yieldVal = stack.Pop();
                stack.Push(new YieldExpr(yieldVal));
                break;

            case OpCodes.MAKE_CELL or OpCodes.COPY_FREE_VARS or OpCodes.RETURN_GENERATOR:
                break;

            case OpCodes.RESUME or OpCodes.NOP or OpCodes.CACHE or OpCodes.EXTENDED_ARG:
                break;
        }
    }

    private static string GetCompareOpString(PythonInstruction ins)
    {
        if (ins.OpCode == OpCodes.IS_OP)
        {
            return (ins.OpArg ?? 0) == 1 ? "is not" : "is";
        }
        if (ins.OpCode == OpCodes.CONTAINS_OP)
        {
            return (ins.OpArg ?? 0) == 1 ? "not in" : "in";
        }
        return ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "==";
    }

    private bool TryDecompileExceptReturn(int idx, SymbolicStack stack, List<StmtNode> stmts, out int nextIdx)
    {
        nextIdx = idx;
        if (_code.Name == "<module>") return false;
        var ins = _instrs[idx];

        if (ins.OpCode == OpCodes.SWAP && (ins.OpArg ?? 2) == 2)
        {
            var p = idx + 1;
            while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
            if (p < _instrs.Count && _instrs[p].OpCode == OpCodes.POP_EXCEPT)
            {
                p++;
                while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
                SkipSyntheticVarCleanup(ref p);
                while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
                if (p < _instrs.Count && _instrs[p].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                {
                    var retVal = _instrs[p].OpCode == OpCodes.RETURN_CONST ? new ConstantExpr(_instrs[p].ArgVal) : (stack.Count > 0 ? stack.Pop() : null);
                    stmts.Add(new ReturnStmt(retVal) { FromReturnConst = _instrs[p].OpCode == OpCodes.RETURN_CONST });
                    nextIdx = p + 1;
                    return true;
                }
            }
        }
        else if (ins.OpCode == OpCodes.POP_EXCEPT)
        {
            var p = idx + 1;
            while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
            SkipSyntheticVarCleanup(ref p);
            while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
            if (p < _instrs.Count && _instrs[p].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                var retVal = _instrs[p].OpCode == OpCodes.RETURN_CONST ? (_instrs[p].ArgVal != null ? new ConstantExpr(_instrs[p].ArgVal) : null) : (stack.Count > 0 ? stack.Pop() : null);
                stmts.Add(new ReturnStmt(retVal));
                nextIdx = p + 1;
                return true;
            }
        }

        return false;
    }

    private bool TryMatchChainedBranch(int idx, SymbolicStack stack, out ExprNode condition, out int trueOffset, out int falseOffset, out int nextIdx, out bool isAssert)
    {
        condition = null!;
        trueOffset = -1;
        falseOffset = -1;
        nextIdx = idx;
        isAssert = false;

        if (stack.Count < 2) return false;
        if (idx + 3 >= _instrs.Count) return false;
        if (_instrs[idx].OpCode != OpCodes.SWAP || (_instrs[idx].OpArg ?? 0) != 2) return false;
        if (_instrs[idx + 1].OpCode != OpCodes.COPY || (_instrs[idx + 1].OpArg ?? 0) != 2) return false;
        if (_instrs[idx + 2].OpCode is not (OpCodes.COMPARE_OP or OpCodes.IS_OP or OpCodes.CONTAINS_OP)) return false;
        if (_instrs[idx + 3].OpCode != OpCodes.POP_JUMP_IF_FALSE) return false;

        var op1 = GetCompareOpString(_instrs[idx + 2]);
        var firstFalseTarget = _instrs[idx + 3].TargetOffset ?? 0;

        var second = stack.Pop();
        var first = stack.Pop();
        var comparators = new List<(string Op, ExprNode Right)> { (op1, second) };

        var curIdx = idx + 4;
        var evalStack = new SymbolicStack();
        var finalFalseTarget = firstFalseTarget;
        var finalJumpIsTrue = false;
        var matched = false;

        while (curIdx < _instrs.Count)
        {
            if (_instrs[curIdx].OpCode == OpCodes.SWAP && (_instrs[curIdx].OpArg ?? 0) == 2 &&
                curIdx + 3 < _instrs.Count &&
                _instrs[curIdx + 1].OpCode == OpCodes.COPY && (_instrs[curIdx + 1].OpArg ?? 0) == 2 &&
                _instrs[curIdx + 2].OpCode is OpCodes.COMPARE_OP or OpCodes.IS_OP or OpCodes.CONTAINS_OP &&
                _instrs[curIdx + 3].OpCode == OpCodes.POP_JUMP_IF_FALSE)
            {
                if (evalStack.Count == 0)
                {
                    throw new InvalidOperationException("chained comparison operand missing");
                }
                var nextRight = evalStack.Pop();
                var nextOp = GetCompareOpString(_instrs[curIdx + 2]);
                comparators.Add((nextOp, nextRight));
                curIdx += 4;
                evalStack.Clear();
                continue;
            }

            if (_instrs[curIdx].OpCode is OpCodes.COMPARE_OP or OpCodes.IS_OP or OpCodes.CONTAINS_OP &&
                curIdx + 1 < _instrs.Count &&
                _instrs[curIdx + 1].OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE)
            {
                if (evalStack.Count == 0)
                {
                    throw new InvalidOperationException("chained comparison final operand missing");
                }
                var lastRight = evalStack.Pop();
                var lastOp = GetCompareOpString(_instrs[curIdx]);
                comparators.Add((lastOp, lastRight));
                finalFalseTarget = _instrs[curIdx + 1].TargetOffset ?? 0;
                finalJumpIsTrue = _instrs[curIdx + 1].OpCode == OpCodes.POP_JUMP_IF_TRUE;
                curIdx += 2;
                matched = true;
                break;
            }

            if (TryDecompileWalrus(ref curIdx, evalStack))
            {
                continue;
            }

            ExecuteInstruction(_instrs[curIdx], evalStack, null);
            curIdx++;
        }

        if (matched && curIdx < _instrs.Count && _instrs[curIdx].OpCode == OpCodes.LOAD_ASSERTION_ERROR &&
            curIdx + 1 < _instrs.Count && _instrs[curIdx + 1].OpCode == OpCodes.RAISE_VARARGS)
        {
            isAssert = true;
            var isTrueJump = _instrs[curIdx - 1].OpCode == OpCodes.POP_JUMP_IF_TRUE;
            condition = isTrueJump
                ? new CompareExpr(first, comparators)
                : InvertCondition(new CompareExpr(first, comparators));
            trueOffset = finalFalseTarget;
            falseOffset = _instrs[curIdx].Offset;
            nextIdx = _offsetToIndex.TryGetValue(trueOffset, out var tIdx) ? tIdx : curIdx + 2;
            return true;
        }

        if (matched && curIdx < _instrs.Count && _instrs[curIdx].OpCode == OpCodes.JUMP_FORWARD)
        {


            if (finalJumpIsTrue)
            {
                trueOffset = finalFalseTarget;
                falseOffset = _instrs[curIdx].TargetOffset ?? 0;
            }
            else
            {
                trueOffset = _instrs[curIdx].TargetOffset ?? 0;
                falseOffset = finalFalseTarget;
            }
            condition = new CompareExpr(first, comparators);
            var continuationOffset = finalJumpIsTrue ? falseOffset : trueOffset;
            nextIdx = _offsetToIndex.TryGetValue(continuationOffset, out var tIdx) ? tIdx : curIdx + 1;
            return true;
        }

        stack.Push(first);
        stack.Push(second);
        return false;
    }

    private bool TryMatchChainLink(int idx, out string op, out int linkLen, out int jumpTarget, out bool hasCopy)
    {
        op = "";
        linkLen = 0;
        jumpTarget = -1;
        hasCopy = false;
        if (idx + 5 >= _instrs.Count) return false;

        if (_instrs[idx].OpCode != OpCodes.SWAP || (_instrs[idx].OpArg ?? 0) != 2) return false;
        if (idx + 3 >= _instrs.Count) return false;
        if (_instrs[idx + 1].OpCode != OpCodes.COPY || (_instrs[idx + 1].OpArg ?? 0) != 2) return false;
        if (_instrs[idx + 2].OpCode is not (OpCodes.COMPARE_OP or OpCodes.IS_OP or OpCodes.CONTAINS_OP)) return false;
        if (_instrs[idx + 3].OpCode != OpCodes.COPY || (_instrs[idx + 3].OpArg ?? 0) != 1) return false;


        var p = idx + 4;
        while (p < _instrs.Count && _instrs[p].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) p++;
        if (p >= _instrs.Count || _instrs[p].OpCode != OpCodes.POP_JUMP_IF_FALSE) return false;
        var jumpIdx = p++;
        while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.NOT_TAKEN) p++;
        if (p >= _instrs.Count || _instrs[p].OpCode != OpCodes.POP_TOP) return false;

        op = GetCompareOpString(_instrs[idx + 2]);
        hasCopy = true;
        jumpTarget = _instrs[jumpIdx].TargetOffset ?? 0;
        linkLen = p + 1 - idx;
        return true;
    }

    private bool TryMatchLegacyChainLink(int idx, out string op, out int linkLen, out int jumpTarget)
    {
        op = "";
        linkLen = 0;
        jumpTarget = -1;
        if (idx + 5 >= _instrs.Count) return false;
        if (_instrs[idx].OpCode != OpCodes.LEGACY_DUP_TOP) return false;
        if (_instrs[idx + 1].OpCode != OpCodes.LEGACY_ROT_THREE) return false;
        if (_instrs[idx + 2].OpCode is not (OpCodes.COMPARE_OP or OpCodes.IS_OP or OpCodes.CONTAINS_OP)) return false;
        if (_instrs[idx + 3].OpCode != OpCodes.COPY || (_instrs[idx + 3].OpArg ?? 0) != 1) return false;
        if (_instrs[idx + 4].OpCode != OpCodes.POP_JUMP_IF_FALSE) return false;
        if (_instrs[idx + 5].OpCode != OpCodes.POP_TOP) return false;
        op = GetCompareOpString(_instrs[idx + 2]);
        jumpTarget = _instrs[idx + 4].TargetOffset ?? 0;
        linkLen = 6;
        return true;
    }

    private bool TryMatchAnyChainLink(int idx, out string op, out int linkLen, out int exitOffset, out bool hasCopy)
    {
        if (TryMatchChainLink(idx, out op, out linkLen, out exitOffset, out hasCopy)) return true;
        if (TryMatchLegacyChainLink(idx, out op, out linkLen, out exitOffset))
        {
            hasCopy = true;
            return true;
        }
        return false;
    }

    private bool TryDecompileChainedComparison(int idx, SymbolicStack stack, out ExprNode chainedExpr, out int nextIdx)
    {
        chainedExpr = null!;
        nextIdx = idx;

        var isLegacyChain = TryMatchLegacyChainLink(idx, out var op1, out var linkLen, out var exitOffset);
        if (!isLegacyChain &&
            !TryMatchChainLink(idx, out op1, out linkLen, out exitOffset, out _))
        {
            return false;
        }
        var hasCopy = true;
        if (stack.Count < 2) return false;

        var second = stack.Pop();
        var first = stack.Pop();
        if (!_offsetToIndex.TryGetValue(exitOffset, out var exitIdx)) return false;

        var comparators = new List<(string Op, ExprNode Right)> { (op1, second) };
        var curIdx = idx + linkLen;
        var subStack = new SymbolicStack();

        while (curIdx < _instrs.Count)
        {
            if (TryMatchAnyChainLink(curIdx, out var nextOp, out var nextLen, out _, out _))
            {
                var nextRight = subStack.Pop();
                comparators.Add((nextOp, nextRight));
                curIdx += nextLen;
                continue;
            }

            if (_instrs[curIdx].OpCode is OpCodes.COMPARE_OP or OpCodes.IS_OP or OpCodes.CONTAINS_OP)
            {
                var lastOp = GetCompareOpString(_instrs[curIdx]);
                if (subStack.Count == 0)
                {
                    throw new InvalidOperationException("chained comparison operand missing");
                }
                var lastRight = subStack.Pop();
                comparators.Add((lastOp, lastRight));
                curIdx++;


                if (isLegacyChain && curIdx < _instrs.Count && _instrs[curIdx].OpCode == OpCodes.JUMP_FORWARD &&
                    _instrs[curIdx].TargetOffset is int chainEndTarget &&
                    _offsetToIndex.TryGetValue(chainEndTarget, out var chainEndIdx))
                {
                    curIdx = chainEndIdx;
                    hasCopy = false;
                }
                break;
            }

            if (TryDecompileWalrus(ref curIdx, subStack))
            {
                continue;
            }

            ExecuteInstruction(_instrs[curIdx], subStack, null);
            curIdx++;
        }

        chainedExpr = new CompareExpr(first, comparators);
        if (hasCopy)
        {
            var afterExitIdx = exitIdx;
            while (afterExitIdx < _instrs.Count && _instrs[afterExitIdx].OpCode is OpCodes.SWAP or OpCodes.POP_TOP)
            {
                afterExitIdx++;
            }
            nextIdx = Math.Max(curIdx, afterExitIdx);
        }
        else
        {
            nextIdx = curIdx;
        }
        return true;
    }

    private static bool IsAssertionErrorLoad(PythonInstruction ins)
    {
        if (ins.OpCode == OpCodes.LOAD_ASSERTION_ERROR) return true;
        if (ins.OpCode is not (OpCodes.LOAD_GLOBAL or OpCodes.LOAD_NAME)) return false;
        var name = ins.ArgRepr ?? ins.ArgVal?.ToString() ?? "";
        return name == "AssertionError";
    }

    private bool TryDecompileAssert(int idx, SymbolicStack stack, List<StmtNode> stmts, out int nextIdx)
    {
        nextIdx = idx;
        var ins = _instrs[idx];
        if (Environment.GetEnvironmentVariable("PYCRUNE_TRACE") == "1")
            Console.Error.WriteLine($"[trace] TryDecompileAssert idx={idx} off={ins.Offset} op={ins.OpCode} stack={stack.Count}");
        if (ins.OpCode is not (OpCodes.POP_JUMP_IF_TRUE or OpCodes.POP_JUMP_IF_FALSE or
                               OpCodes.POP_JUMP_IF_NONE or OpCodes.POP_JUMP_IF_NOT_NONE)) return false;

        var targetOffset = ins.TargetOffset ?? 0;
        if (!_offsetToIndex.TryGetValue(targetOffset, out var targetIdx)) return false;

        if (idx + 1 < _instrs.Count && IsAssertionErrorLoad(_instrs[idx + 1]))
        {
            if (stack.Count == 0) return false;
            var cond = stack.Pop();


            if (ins.OpCode == OpCodes.POP_JUMP_IF_FALSE)
            {
                cond = new UnaryExpr("not", cond);
            }
            else if (ins.OpCode == OpCodes.POP_JUMP_IF_NONE)
            {
                cond = new CompareExpr(cond, [("is", new ConstantExpr(null))]);
            }
            else if (ins.OpCode == OpCodes.POP_JUMP_IF_NOT_NONE)
            {
                cond = new CompareExpr(cond, [("is not", new ConstantExpr(null))]);
            }

            ExprNode? msg = null;
            var raiseIdx = idx + 2;
            while (raiseIdx < targetIdx && _instrs[raiseIdx].OpCode != OpCodes.RAISE_VARARGS) raiseIdx++;
            if (raiseIdx > idx + 2 && raiseIdx < targetIdx)
            {


                var msgStart = idx + 2;
                if (raiseIdx - 1 > idx && _instrs[raiseIdx - 1].OpCode == OpCodes.CALL &&
                    (_instrs[raiseIdx - 1].OpArg ?? 0) > 0)
                {
                    msgStart = idx + 1;
                }
                var msgStack = new SymbolicStack();
                DecompileRange(msgStart, raiseIdx, msgStack);
                if (msgStack.Count > 0)
                {
                    msg = msgStack.Pop();
                }
            }

            if (msg is CallExpr ce)
            {
                var isAssertCtor = ce.Function is AssertionErrorExpr ||
                                   ce.Function is NameExpr { Name: "AssertionError" };
                if (isAssertCtor && ce.Arguments.Count == 1)
                {
                    msg = ce.Arguments[0];
                }
                else if (ce.Arguments.Count == 0)
                {
                    msg = ce.Function;
                }
            }

            stmts.Add(new AssertStmt(cond, msg));
            nextIdx = targetIdx;
            return true;
        }

        return false;
    }

        private static bool TryBuildAssertFromRaise(ExprNode? exc, List<StmtNode>? stmts)
    {
        if (stmts == null) return false;
        switch (exc)
        {
            case AssertionErrorExpr:
                stmts.Add(new AssertStmt(new ConstantExpr(false), null));
                return true;
            case CallExpr { Function: AssertionErrorExpr } ce:
                stmts.Add(new AssertStmt(new ConstantExpr(false), ce.Arguments.Count > 0 ? ce.Arguments[0] : null));
                return true;
            default:
                return false;
        }
    }

    private bool TryDecompileImport(int idx, SymbolicStack stack, List<StmtNode> stmts, out int nextIdx)
    {
        nextIdx = idx;


        var p = idx;
        while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
        if (p >= _instrs.Count || _instrs[p].OpCode != OpCodes.LOAD_CONST) return false;
        var levelIns = _instrs[p];
        p++;
        while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
        if (p >= _instrs.Count || _instrs[p].OpCode != OpCodes.LOAD_CONST) return false;
        var fromListIns = _instrs[p];
        p++;
        while (p < _instrs.Count && _instrs[p].OpCode == OpCodes.EXTENDED_ARG) p++;
        if (p >= _instrs.Count || _instrs[p].OpCode != OpCodes.IMPORT_NAME) return false;

        var level = levelIns.ArgVal is int l ? l : (levelIns.ArgVal is long l2 ? (int)l2 : 0);
        var fromListVal = fromListIns.ArgVal;
        var modName = _instrs[p].ArgVal?.ToString() ?? _instrs[p].ArgRepr ?? "";
        if (modName.EndsWith(" + eager")) modName = modName[..^8];
        var cur = p + 1;
        while (cur < _instrs.Count && _instrs[cur].OpCode == OpCodes.EXTENDED_ARG) cur++;


        if (cur + 1 < _instrs.Count &&
            _instrs[cur].OpCode == OpCodes.CALL_INTRINSIC_1 && (_instrs[cur].OpArg ?? 0) == 2 &&
            _instrs[cur + 1].OpCode == OpCodes.POP_TOP)
        {
            stmts.Add(new ImportFromStmt(modName, [new ImportAlias("*", null)], level));
            nextIdx = cur + 2;
            return true;
        }

        if (fromListVal == null)
        {
            if (cur < _instrs.Count && _instrs[cur].OpCode is OpCodes.STORE_NAME or OpCodes.STORE_FAST or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
            {
                var targetName = _instrs[cur].ArgRepr ?? _instrs[cur].ArgVal?.ToString() ?? "";
                var asName = targetName != modName ? targetName : null;
                stmts.Add(new ImportStmt([new ImportAlias(modName, asName)]));
                nextIdx = cur + 1;
                return true;
            }

            var afterFrom = cur;
            while (afterFrom < _instrs.Count && _instrs[afterFrom].OpCode == OpCodes.EXTENDED_ARG) afterFrom++;
            if (afterFrom + 2 < _instrs.Count &&
                _instrs[afterFrom].OpCode == OpCodes.IMPORT_FROM &&
                _instrs[afterFrom + 1].OpCode is OpCodes.STORE_NAME or OpCodes.STORE_FAST or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF &&
                _instrs[afterFrom + 2].OpCode == OpCodes.POP_TOP)
            {
                var asName = _instrs[afterFrom + 1].ArgRepr ?? _instrs[afterFrom + 1].ArgVal?.ToString() ?? "";
                stmts.Add(new ImportStmt([new ImportAlias(modName, asName)]));
                nextIdx = afterFrom + 3;
                return true;
            }
        }
        else
        {
            var aliases = new List<ImportAlias>();
            while (cur + 1 < _instrs.Count && _instrs[cur].OpCode == OpCodes.IMPORT_FROM && _instrs[cur + 1].OpCode is OpCodes.STORE_NAME or OpCodes.STORE_FAST or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
            {
                var importedName = _instrs[cur].ArgRepr ?? _instrs[cur].ArgVal?.ToString() ?? "";
                var storedName = _instrs[cur + 1].ArgRepr ?? _instrs[cur + 1].ArgVal?.ToString() ?? "";
                var asName = storedName != importedName ? storedName : null;
                aliases.Add(new ImportAlias(importedName, asName));
                cur += 2;
                while (cur < _instrs.Count && _instrs[cur].OpCode == OpCodes.EXTENDED_ARG) cur++;
            }
            if (cur < _instrs.Count && _instrs[cur].OpCode == OpCodes.POP_TOP)
            {
                stmts.Add(new ImportFromStmt(modName, aliases, level));
                nextIdx = cur + 1;
                return true;
            }
        }

        return false;
    }


    private bool TryDecompileWith314(int idx, int endIdx, SymbolicStack stack, out StmtNode withStmt, out int nextIdx)
    {
        withStmt = null!;
        nextIdx = idx;
        if (idx + 5 >= endIdx || stack.Count == 0) return false;
        if (_instrs[idx].OpCode != OpCodes.COPY || (_instrs[idx].OpArg ?? 0) != 1) return false;
        if (_instrs[idx + 1].OpCode != OpCodes.LOAD_SPECIAL) return false;

        var exitName = _instrs[idx + 1].ArgRepr ?? _instrs[idx + 1].ArgVal?.ToString() ?? "";
        var isAsync = exitName == "__aexit__";
        if (!isAsync && exitName != "__exit__") return false;
        var enterName = isAsync ? "__aenter__" : "__enter__";
        if (_instrs[idx + 2].OpCode != OpCodes.SWAP || (_instrs[idx + 2].OpArg ?? 0) != 2) return false;
        if (_instrs[idx + 3].OpCode != OpCodes.SWAP || (_instrs[idx + 3].OpArg ?? 0) != 3) return false;
        if (_instrs[idx + 4].OpCode != OpCodes.LOAD_SPECIAL) return false;
        var actualEnter = _instrs[idx + 4].ArgRepr ?? _instrs[idx + 4].ArgVal?.ToString() ?? "";
        if (actualEnter != enterName) return false;
        if (_instrs[idx + 5].OpCode != OpCodes.CALL || (_instrs[idx + 5].OpArg ?? 0) != 0) return false;

        var items = new List<WithItem>();
        var ctxExpr = stack.Peek();
        var headerAt = idx;
        var cur = idx + 6;
        var guard = 0;
        while (guard++ < 32)
        {
            if (isAsync)
            {
                cur = SkipWithAwait314(cur, endIdx);
                if (cur < 0) return false;
            }

            ExprNode? target = null;
            if (cur < endIdx && _instrs[cur].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
            {
                target = new NameExpr(_instrs[cur].ArgRepr ?? _instrs[cur].ArgVal?.ToString() ?? "");
                cur++;
            }
            else if (cur < endIdx && _instrs[cur].OpCode == OpCodes.POP_TOP)
            {
                cur++;
            }
            else
            {
                return false;
            }
            items.Add(new WithItem(ctxExpr, target));


            if (target == null || cur >= endIdx || _instrs[cur].Offset != _instrs[cur - 1].Offset)
                break;


            var nextCopy = -1;
            for (var p = cur; p + 1 < endIdx && p < cur + 64; p++)
            {
                if (_instrs[p].OpCode is OpCodes.FOR_ITER or OpCodes.GET_AITER or OpCodes.GET_ANEXT or
                    OpCodes.SEND or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or
                    OpCodes.RAISE_VARARGS or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_FORWARD or
                    OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
                {
                    break;
                }
                if (_instrs[p].OpCode == OpCodes.COPY && (_instrs[p].OpArg ?? 0) == 1 &&
                    _instrs[p + 1].OpCode == OpCodes.LOAD_SPECIAL)
                {
                    nextCopy = p;
                    break;
                }
            }
            if (nextCopy < 0) break;

            if (!IsWithHeader314(nextCopy, endIdx, isAsync)) break;

            var nextStack = new SymbolicStack();
            try
            {
                DecompileRange(cur, nextCopy, nextStack);
            }
            catch (Exception)
            {
                break;
            }
            if (nextStack.Count == 0) break;
            ctxExpr = nextStack.Pop();
            headerAt = nextCopy;
            cur = nextCopy + 6;
        }

        if (!Is311() && !IsAtLeast314() && items.Count > 1) return false;
        var bodyStart = cur;
        if (bodyStart >= endIdx) return false;
        var bodyProbe = bodyStart;
        while (bodyProbe < endIdx && _instrs[bodyProbe].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) bodyProbe++;
        if (bodyProbe >= endIdx) return false;
        var bodyOffset = _instrs[bodyProbe].Offset;


        var handlerIdx = -1;
        var handlerOffset = -1;
        var bodyStartOffset = _instrs[bodyStart].Offset;
        var bestEntryStart = -1;
        foreach (var entry in _code.ExceptionTable)
        {
            if (entry.Start > bodyOffset || entry.End < bodyStartOffset) continue;
            if (!_offsetToIndex.TryGetValue(entry.Target, out var hIdx) || hIdx + 1 >= _instrs.Count) continue;
            if (_instrs[hIdx].OpCode != OpCodes.PUSH_EXC_INFO ||
                _instrs[hIdx + 1].OpCode != OpCodes.WITH_EXCEPT_START) continue;
            if (entry.Start > bestEntryStart)
            {
                bestEntryStart = entry.Start;
                handlerIdx = hIdx;
                handlerOffset = entry.Target;
            }
        }

        var cleanupLimit = handlerIdx >= 0 ? Math.Min(handlerIdx, endIdx) : endIdx;
        var allCleanupStarts = FindWithCleanupStarts314(bodyStart, cleanupLimit, int.MaxValue);
        var cleanupStarts = new List<int>();
        if (handlerOffset >= 0)
        {
            if (items.Count > 1)
            {


                var first = Math.Max(0, allCleanupStarts.Count - items.Count);
                for (var c = first; c < allCleanupStarts.Count; c++)
                {
                    cleanupStarts.Add(allCleanupStarts[c]);
                }
            }
            else
            {


                var anchor = -1;
                for (var c = allCleanupStarts.Count - 1; c >= 0; c--)
                {
                    if (IsNestedWithCleanup314(allCleanupStarts[c], handlerOffset)) continue;
                    if (IsNormalWithExit314(allCleanupStarts[c], handlerIdx, isAsync, endIdx))
                    {
                        anchor = c;
                        break;
                    }
                }
                if (anchor < 0)
                {
                    for (var c = allCleanupStarts.Count - 1; c >= 0; c--)
                    {
                        if (!IsNestedWithCleanup314(allCleanupStarts[c], handlerOffset)) { anchor = c; break; }
                    }
                }
                if (anchor >= 0) cleanupStarts.Add(allCleanupStarts[anchor]);
            }
        }
        else if (allCleanupStarts.Count > items.Count)
        {
            cleanupStarts = allCleanupStarts.Skip(allCleanupStarts.Count - items.Count).ToList();
        }
        else
        {
            cleanupStarts = allCleanupStarts;
        }

        if (handlerIdx < 0)
        {


            if (cleanupStarts.Count > 0)
            {
                var lastCleanup = cleanupStarts[^1];
                for (var i = lastCleanup; i < _instrs.Count; i++)
                {
                    if (_instrs[i].OpCode != OpCodes.PUSH_EXC_INFO) continue;
                    if (_code.ExceptionTable.Any(e => e.Target == _instrs[i].Offset))
                    {
                        handlerIdx = i;
                        break;
                    }
                }
            }
            if (handlerIdx < 0)
            {
                var handlerTarget = -1;
                foreach (var entry in _code.ExceptionTable)
                {
                    if ((!entry.Encloses(bodyOffset) && entry.Start != bodyOffset) || entry.Target <= bodyOffset) continue;
                    if (!_offsetToIndex.TryGetValue(entry.Target, out var hIdx) || hIdx >= _instrs.Count) continue;
                    if (_instrs[hIdx].OpCode != OpCodes.PUSH_EXC_INFO) continue;
                    if (entry.Target > handlerTarget)
                    {
                        handlerTarget = entry.Target;
                        handlerIdx = hIdx;
                    }
                }
            }
            if (handlerIdx < 0) return false;
            cleanupStarts = FindWithCleanupStarts314(bodyStart, handlerIdx, items.Count);
        }
        if (cleanupStarts.Count != items.Count || cleanupStarts[0] < bodyStart)
        {


            if (cleanupStarts.Count == 0 && items.Count == 1 && handlerIdx > bodyStart)
            {
                var abruptBody = DecompileRange(bodyStart, handlerIdx, stack.Clone());
                var afterHandler = FindHandlerNormalExit(handlerIdx, endIdx);
                if (afterHandler < 0) return false;
                while (afterHandler < endIdx && _instrs[afterHandler].OpCode is OpCodes.POP_TOP or OpCodes.NOP or OpCodes.NOT_TAKEN)
                    afterHandler++;
                if (afterHandler < endIdx && _instrs[afterHandler].OpCode == OpCodes.JUMP_FORWARD &&
                    _instrs[afterHandler].TargetOffset is int jt && _offsetToIndex.TryGetValue(jt, out var jIdx))
                {
                    afterHandler = jIdx;
                }
                stack.Pop();
                withStmt = new WithStmt(items, abruptBody, isAsync);
                nextIdx = afterHandler;
                return true;
            }
            return false;
        }

        var bodyStack = new SymbolicStack();
        var body = DecompileRange(bodyStart, cleanupStarts[0], bodyStack);
        var bodyReturnIdx = -1;
        for (var q = cleanupStarts[0]; q < handlerIdx; q++)
        {
            if (_instrs[q].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                bodyReturnIdx = q;
                break;
            }
        }
        var bodyHasReturn = bodyReturnIdx >= 0 && bodyStack.Count > 0;
        if (bodyHasReturn) body.Add(new ReturnStmt(bodyStack.Pop()));
        if (bodyHasReturn)
        {
            _handlerStopIndices.Add(handlerIdx);
            stack.Pop();
            withStmt = new WithStmt(items, body, isAsync);
            nextIdx = bodyReturnIdx + 1;
            return true;
        }

        var after = cleanupStarts[0];
        foreach (var _ in cleanupStarts)
        {
            while (after < endIdx && !(_instrs[after].OpCode == OpCodes.CALL && (_instrs[after].OpArg ?? 0) == 3)) after++;
            if (after >= endIdx) return false;
            after++;
            if (isAsync)
            {
                after = SkipWithAwait314(after, endIdx);
                if (after < 0) return false;
            }
            if (after < endIdx && _instrs[after].OpCode == OpCodes.POP_TOP) after++;
        }


        if (body.Count == 0 && after < _instrs.Count && _activeLoopExits.Contains(_instrs[after].Offset))
        {
            body.Add(new BreakStmt());
        }
        stack.Pop();
        withStmt = new WithStmt(items, body, isAsync);
        nextIdx = after;
        return true;
    }

    private int SkipWithAwait314(int cur, int endIdx)
    {
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.GET_AWAITABLE) return -1;
        cur++;
        while (cur < endIdx && _instrs[cur].OpCode is OpCodes.PUSH_NULL or OpCodes.NOP or OpCodes.NOT_TAKEN) cur++;
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.LOAD_CONST) return -1;
        cur++;
        while (cur < endIdx && _instrs[cur].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) cur++;
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.SEND) return -1;
        if (_instrs[cur].TargetOffset is not int target || !_offsetToIndex.TryGetValue(target, out var endSendIdx))
            return -1;
        if (endSendIdx >= endIdx || _instrs[endSendIdx].OpCode != OpCodes.END_SEND) return -1;
        return endSendIdx + 1;
    }

    private bool IsWithHeader314(int at, int endIdx, bool isAsync)
    {
        if (at + 5 >= endIdx) return false;
        if (_instrs[at].OpCode != OpCodes.COPY || (_instrs[at].OpArg ?? 0) != 1) return false;
        if (_instrs[at + 1].OpCode != OpCodes.LOAD_SPECIAL) return false;
        var exitName = _instrs[at + 1].ArgRepr ?? _instrs[at + 1].ArgVal?.ToString() ?? "";
        if (exitName != (isAsync ? "__aexit__" : "__exit__")) return false;
        if (_instrs[at + 2].OpCode != OpCodes.SWAP || (_instrs[at + 2].OpArg ?? 0) != 2) return false;
        if (_instrs[at + 3].OpCode != OpCodes.SWAP || (_instrs[at + 3].OpArg ?? 0) != 3) return false;
        if (_instrs[at + 4].OpCode != OpCodes.LOAD_SPECIAL) return false;
        var enterName = _instrs[at + 4].ArgRepr ?? _instrs[at + 4].ArgVal?.ToString() ?? "";
        if (enterName != (isAsync ? "__aenter__" : "__enter__")) return false;
        return _instrs[at + 5].OpCode == OpCodes.CALL && (_instrs[at + 5].OpArg ?? 0) == 0;
    }

    private bool IsNestedWithCleanup314(int cleanupIdx, int handlerOffset)
    {


        var startOffset = _instrs[cleanupIdx].Offset;
        return _code.ExceptionTable.Any(e =>
            e.Target <= handlerOffset && e.Start <= startOffset && startOffset < e.End);
    }

    private bool IsNormalWithExit314(int cleanupIdx, int handlerIdx, bool isAsync, int endIdx)
    {
        var exitIdx = FindHandlerNormalExit(handlerIdx, _instrs.Count);
        if (exitIdx < 0) return false;
        var continuation = _instrs[exitIdx].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT
            ? _instrs[exitIdx].TargetOffset ?? -1
            : _instrs[exitIdx].Offset;
        if (continuation < 0) return false;

        var limit = _instrs.Count;
        var after = cleanupIdx;
        while (after < limit && !(_instrs[after].OpCode == OpCodes.CALL && (_instrs[after].OpArg ?? 0) == 3)) after++;
        if (after >= limit) return false;
        after++;
        if (isAsync)
        {
            after = SkipWithAwait314(after, limit);
            if (after < 0) return false;
        }
        if (after < limit && _instrs[after].OpCode == OpCodes.POP_TOP) after++;
        if (after >= limit) return false;
        if (_instrs[after].Offset == continuation) return true;
        return _instrs[after].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT &&
               (_instrs[after].TargetOffset ?? -1) == continuation;
    }

    private List<int> FindWithCleanupStarts314(int bodyStart, int handlerIdx, int count)
    {
        var starts = new List<int>();
        for (var i = bodyStart; i < handlerIdx; i++)
        {
            if (_instrs[i].OpCode != OpCodes.CALL || (_instrs[i].OpArg ?? 0) != 3) continue;
            var q = i - 1;
            var consts = 0;
            var firstConst = -1;
            while (q >= bodyStart && _instrs[q].OpCode is OpCodes.LOAD_CONST or OpCodes.PUSH_NULL or OpCodes.NOP or OpCodes.NOT_TAKEN)
            {
                if (_instrs[q].OpCode == OpCodes.LOAD_CONST)
                {
                    consts++;
                    firstConst = q;
                }
                q--;
            }
            if (consts < 3) continue;


            var start = firstConst;
            var before = firstConst - 1;
            while (before >= bodyStart && _instrs[before].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) before--;


            if (before >= bodyStart && _instrs[before].OpCode == OpCodes.SWAP && (_instrs[before].OpArg ?? 0) == 2)
            {
                before--;
                while (before >= bodyStart && _instrs[before].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) before--;
                if (before >= bodyStart && _instrs[before].OpCode == OpCodes.SWAP && (_instrs[before].OpArg ?? 0) == 3)
                {
                    start = before;
                }
            }
            starts.Add(start);
        }
        if (starts.Count > count) starts.RemoveRange(0, starts.Count - count);
        return starts;
    }


    private bool TryDecompileWith311(int idx, int endIdx, SymbolicStack stack, out StmtNode withStmt, out int nextIdx)
    {
        withStmt = null!;
        nextIdx = idx;
        if (!IsAtLeast311()) return false;
        if (idx >= _instrs.Count || _instrs[idx].OpCode != OpCodes.BEFORE_WITH) return false;
        if (stack.Count == 0) return false;

        var items = new List<WithItem>();
        var ctxExpr = stack.Peek();
        var cur = idx + 1;
        var guard = 0;
        while (guard++ < 32)
        {
            ExprNode? target = null;
            if (cur < endIdx && _instrs[cur].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
            {
                target = new NameExpr(_instrs[cur].ArgRepr ?? _instrs[cur].ArgVal?.ToString() ?? "");
                cur++;
            }
            else if (cur < endIdx && _instrs[cur].OpCode == OpCodes.POP_TOP)
            {
                cur++;
            }
            items.Add(new WithItem(ctxExpr, target));


            var nextBefore = -1;
            for (var p = cur; p < endIdx && p < cur + 64; p++)
            {
                if (_instrs[p].OpCode == OpCodes.BEFORE_WITH) { nextBefore = p; break; }
            }
            if (nextBefore < 0) break;
            var nextStack = new SymbolicStack();
            try { DecompileRange(cur, nextBefore, nextStack); }
            catch (Exception) { break; }
            if (nextStack.Count == 0) break;
            ctxExpr = nextStack.Pop();
            cur = nextBefore + 1;
        }

        if (!Is311() && !IsAtLeast314() && items.Count > 1) return false;
        var bodyStart = cur;
        if (bodyStart >= endIdx) return false;
        var bodyProbe = bodyStart;
        while (bodyProbe < endIdx && _instrs[bodyProbe].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) bodyProbe++;
        if (bodyProbe >= endIdx) return false;
        var bodyOffset = _instrs[bodyProbe].Offset;
        var handlerIdx = -1;
        var handlerTarget = -1;
        foreach (var entry in _code.ExceptionTable)
        {
            if ((!entry.Encloses(bodyOffset) && entry.Start != bodyOffset) || entry.Target <= bodyOffset) continue;
            if (!_offsetToIndex.TryGetValue(entry.Target, out var hIdx) || hIdx >= _instrs.Count) continue;
            if (_instrs[hIdx].OpCode != OpCodes.PUSH_EXC_INFO) continue;
            if (entry.Target > handlerTarget) { handlerTarget = entry.Target; handlerIdx = hIdx; }
        }
        if (handlerIdx < 0) return false;

        var cleanupStarts = new List<int>();
        var cleanupLimit = items.Count == 1 ? handlerIdx : endIdx;
        for (var i = bodyStart; i < cleanupLimit; i++)
        {
            if (_instrs[i].OpCode != OpCodes.CALL || (_instrs[i].OpArg ?? 0) != 2) continue;
            var q = i - 1;
            var consts = 0;
            while (q >= bodyStart && _instrs[q].OpCode is OpCodes.LOAD_CONST or OpCodes.NOP or OpCodes.NOT_TAKEN)
            {
                if (_instrs[q].OpCode == OpCodes.LOAD_CONST) consts++;
                q--;
            }
            if (consts < 3) continue;
            if (items.Count == 1)
            {
                cleanupStarts.Clear();
                cleanupStarts.Add(q + 1);
            }
            else if (cleanupStarts.Count < items.Count)
            {
                cleanupStarts.Add(q + 1);
            }
        }
        if (cleanupStarts.Count != items.Count)
        {
            if (cleanupStarts.Count == 0 && items.Count == 1 && handlerIdx > bodyStart)
            {
                var abruptBody = DecompileRange(bodyStart, handlerIdx, stack.Clone());
                var afterHandler = FindHandlerNormalExit(handlerIdx, endIdx);
                if (afterHandler < 0) return false;
                while (afterHandler < endIdx && _instrs[afterHandler].OpCode is OpCodes.POP_TOP or OpCodes.NOP or OpCodes.NOT_TAKEN)
                    afterHandler++;
                if (afterHandler < endIdx && _instrs[afterHandler].OpCode == OpCodes.JUMP_FORWARD &&
                    _instrs[afterHandler].TargetOffset is int jt && _offsetToIndex.TryGetValue(jt, out var jIdx))
                {
                    afterHandler = jIdx;
                }
                stack.Pop();
                withStmt = new WithStmt(items, abruptBody, IsAsync: false);
                nextIdx = afterHandler;
                return true;
            }
            return false;
        }

        if (!Is311())
        {
            for (var b = bodyStart; b < cleanupStarts[0]; b++)
            {
                if (_instrs[b].OpCode is OpCodes.BEFORE_WITH or OpCodes.BEFORE_ASYNC_WITH) return false;
            }
        }
        var bodyReturnScanStart = cleanupStarts[0];
        if (items.Count == 1 && cleanupStarts.Count > 1)
        {


            cleanupStarts = [cleanupStarts[^1]];
        }
        var bodyStack = new SymbolicStack();
        var body = DecompileRange(bodyStart, cleanupStarts[0], bodyStack);
        var bodyReturnIdx = -1;
        for (var q = bodyReturnScanStart; q < handlerIdx; q++)
        {
            if (_instrs[q].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                bodyReturnIdx = q;
                break;
            }
        }
        var bodyHasReturn = bodyReturnIdx >= 0 && bodyStack.Count > 0;
        if (bodyHasReturn)
        {
            body.Add(new ReturnStmt(bodyStack.Pop()));
        }
        if (bodyHasReturn)
        {
            _handlerStopIndices.Add(handlerIdx);
            stack.Pop();
            withStmt = new WithStmt(items, body, IsAsync: false);
            nextIdx = bodyReturnIdx + 1;
            return true;
        }

        var after = cleanupStarts[0];
        foreach (var _ in cleanupStarts)
        {
            while (after < endIdx && !(_instrs[after].OpCode == OpCodes.CALL && (_instrs[after].OpArg ?? 0) == 2)) after++;
            if (after >= endIdx) return false;
            after++;
            while (after < endIdx && _instrs[after].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) after++;
            if (after < endIdx && _instrs[after].OpCode == OpCodes.POP_TOP) after++;
            while (after < endIdx && _instrs[after].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) after++;
            if (after < endIdx &&
                _instrs[after].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
            {
                var jt = _instrs[after].TargetOffset ?? -1;
                if (IsActiveLoopHeader(jt))
                {
                    _handlerStopIndices.Add(handlerIdx);
                    after = handlerIdx;
                }
                else if (_activeLoopExits.Contains(jt))
                {

                }
                else if (_offsetToIndex.TryGetValue(jt, out var jIdx))
                {
                    after = jIdx;
                }
            }
        }

        stack.Pop();
        withStmt = new WithStmt(items, body, IsAsync: false);
        nextIdx = after;
        return true;
    }


    private bool TryDecompileAsyncWith311(int idx, int endIdx, SymbolicStack stack, out StmtNode withStmt, out int nextIdx)
    {
        withStmt = null!;
        nextIdx = idx;
        if (idx >= _instrs.Count || _instrs[idx].OpCode != OpCodes.BEFORE_ASYNC_WITH) return false;
        if (stack.Count == 0) return false;

        var cur = idx + 1;
        while (cur < endIdx && _instrs[cur].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) cur++;
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.GET_AWAITABLE) return false;
        cur++;
        while (cur < endIdx && _instrs[cur].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) cur++;
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.LOAD_CONST) return false;
        cur++;
        while (cur < endIdx && _instrs[cur].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) cur++;
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.SEND) return false;
        if (_instrs[cur].TargetOffset is not int sendTarget ||
            !_offsetToIndex.TryGetValue(sendTarget, out var bindIdx))
        {
            return false;
        }

        var bodyStart = bindIdx;
        if (bodyStart < endIdx && _instrs[bodyStart].OpCode == OpCodes.END_SEND) bodyStart++;
        ExprNode? target = null;
        if (bodyStart < endIdx &&
            _instrs[bodyStart].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
        {
            target = new NameExpr(_instrs[bodyStart].ArgRepr ?? _instrs[bodyStart].ArgVal?.ToString() ?? "");
            bodyStart++;
        }
        else if (bodyStart < endIdx && _instrs[bodyStart].OpCode == OpCodes.POP_TOP)
        {
            bodyStart++;
        }
        if (bodyStart >= endIdx) return false;

        var cleanupStart = -1;
        var callIdx = -1;
        var handlerIdx = -1;

        if (Is311())
        {


            for (var i = bodyStart; i < endIdx; i++)
            {
                if (_instrs[i].OpCode == OpCodes.CALL && (_instrs[i].OpArg ?? 0) is 2 or 3)
                {
                    var q = i - 1;
                    var consts = 0;
                    while (q >= bodyStart && _instrs[q].OpCode is OpCodes.LOAD_CONST or OpCodes.PUSH_NULL or OpCodes.NOP or OpCodes.NOT_TAKEN)
                    {
                        if (_instrs[q].OpCode == OpCodes.LOAD_CONST) consts++;
                        q--;
                    }
                    if (consts >= 3) { cleanupStart = q + 1; callIdx = i; }
                }
            }
            if (cleanupStart < 0 || cleanupStart < bodyStart) return false;

            var cleanupCursor = callIdx + 1;
            while (cleanupCursor < endIdx &&
                   _instrs[cleanupCursor].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN) cleanupCursor++;
            if (cleanupCursor < endIdx && _instrs[cleanupCursor].OpCode == OpCodes.GET_AWAITABLE) cleanupCursor++;
            if (cleanupCursor < endIdx && _instrs[cleanupCursor].OpCode == OpCodes.LOAD_CONST) cleanupCursor++;
            if (cleanupCursor < endIdx && _instrs[cleanupCursor].OpCode == OpCodes.SEND &&
                _instrs[cleanupCursor].TargetOffset is int cleanupSendTarget &&
                _offsetToIndex.TryGetValue(cleanupSendTarget, out var cleanupSendIdx))
            {
                cleanupCursor = cleanupSendIdx;
                if (cleanupCursor < endIdx && _instrs[cleanupCursor].OpCode == OpCodes.END_SEND) cleanupCursor++;
            }
            if (cleanupCursor < endIdx && _instrs[cleanupCursor].OpCode == OpCodes.POP_TOP) cleanupCursor++;
            var cleanupEndOffset = cleanupCursor < endIdx ? _instrs[cleanupCursor].Offset : int.MaxValue;

            for (var i = callIdx + 1; i < endIdx; i++)
            {
                if (_instrs[i].OpCode != OpCodes.PUSH_EXC_INFO) continue;
                var handlerOffset = _instrs[i].Offset;
                if (!_code.ExceptionTable.Any(entry => entry.Target == handlerOffset && entry.Start < cleanupEndOffset)) continue;
                if (i > handlerIdx) handlerIdx = i;
            }
        }
        else
        {


            var bodyOffset = _instrs[bodyStart].Offset;
            foreach (var entry in _code.ExceptionTable)
            {
                if ((!entry.Encloses(bodyOffset) &&
                     !(entry.Start < bodyOffset && entry.End >= bodyOffset)) ||
                    entry.Target <= bodyOffset) continue;
                if (!_offsetToIndex.TryGetValue(entry.Target, out var hIdx) || hIdx >= endIdx) continue;
                if (_instrs[hIdx].OpCode == OpCodes.PUSH_EXC_INFO) { handlerIdx = hIdx; break; }
            }
            if (handlerIdx < 0) return false;

            var hasNestedWith = false;
            for (var b = bodyStart; b < handlerIdx; b++)
            {
                if (_instrs[b].OpCode is OpCodes.BEFORE_ASYNC_WITH or OpCodes.BEFORE_WITH)
                {
                    hasNestedWith = true;
                    break;
                }
            }
            for (var i = bodyStart; i < handlerIdx; i++)
            {
                if (_instrs[i].OpCode == OpCodes.CALL && (_instrs[i].OpArg ?? 0) is 2 or 3)
                {
                    var q = i - 1;
                    var consts = 0;
                    while (q >= bodyStart && _instrs[q].OpCode is OpCodes.LOAD_CONST or OpCodes.PUSH_NULL or OpCodes.NOP or OpCodes.NOT_TAKEN)
                    {
                        if (_instrs[q].OpCode == OpCodes.LOAD_CONST) consts++;
                        q--;
                    }
                    if (consts >= 3)
                    {
                        if (!hasNestedWith && cleanupStart >= 0) continue;
                        cleanupStart = q + 1;
                        callIdx = i;
                    }
                }
            }
        }
        if (cleanupStart < 0 || cleanupStart < bodyStart || handlerIdx < 0) return false;

        var context = stack.Peek();
        stack.Pop();
        var bodyStack = new SymbolicStack();
        var body = DecompileRange(bodyStart, cleanupStart, bodyStack);
        var bodyReturnIdx = -1;
        for (var q = cleanupStart; q < handlerIdx; q++)
        {
            if (_instrs[q].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                bodyReturnIdx = q;
                break;
            }
        }
        var bodyHasReturn = bodyReturnIdx >= 0 && bodyStack.Count > 0;
        if (bodyHasReturn)
        {
            body.Add(new ReturnStmt(bodyStack.Pop()));
        }
        if (bodyHasReturn)
        {
            _handlerStopIndices.Add(handlerIdx);
            withStmt = new WithStmt([new WithItem(context, target)], body, IsAsync: true);
            nextIdx = bodyReturnIdx + 1;
            return true;
        }
        var after = FindLegacyAsyncExitResume(callIdx, handlerIdx, endIdx);
        withStmt = new WithStmt([new WithItem(context, target)], body, IsAsync: true);
        nextIdx = after;
        return true;
    }

        private int FindLegacyAsyncExitResume(int callIdx, int handlerIdx, int endIdx)
    {
        var p = SkipTransparent(callIdx + 1);
        if (p < endIdx && _instrs[p].OpCode == OpCodes.GET_AWAITABLE)
        {
            p = SkipTransparent(p + 1);
            if (p < endIdx && _instrs[p].OpCode == OpCodes.LOAD_CONST) p = SkipTransparent(p + 1);
            if (p < endIdx && _instrs[p].OpCode is OpCodes.LEGACY_YIELD_FROM or OpCodes.SEND)
            {
                if (_instrs[p].OpCode == OpCodes.SEND &&
                    _instrs[p].TargetOffset is int sendTarget &&
                    _offsetToIndex.TryGetValue(sendTarget, out var sendEnd))
                {
                    p = sendEnd;
                    if (p < endIdx && _instrs[p].OpCode == OpCodes.END_SEND) p++;
                }
                else
                {
                    p = SkipTransparent(p + 1);
                }
            }
            p = SkipTransparent(p);
            if (p < endIdx && _instrs[p].OpCode == OpCodes.POP_TOP) p++;
            p = SkipTransparent(p);
        }

        if (p < endIdx &&
            _instrs[p].OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
        {
            var target = _instrs[p].TargetOffset ?? -1;
            if (IsActiveLoopHeader(target))
            {
                var h = handlerIdx;
                while (h < endIdx)
                {
                    if (_instrs[h].OpCode is OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT or OpCodes.JUMP_FORWARD &&
                        _instrs[h].TargetOffset == target)
                    {
                        return h + 1;
                    }
                    if (_instrs[h].OpCode == OpCodes.END_ASYNC_FOR) break;
                    h++;
                }
                return endIdx;
            }
            if (_offsetToIndex.TryGetValue(target, out var targetIdx)) return targetIdx;
        }


        _handlerStopIndices.Add(handlerIdx);
        return p;
    }


    private bool TryDecompileLegacyAsyncWith(int idx, int endIdx, SymbolicStack stack, out StmtNode withStmt, out int nextIdx)
    {
        withStmt = null!;
        nextIdx = idx;
        if (idx >= _instrs.Count || _instrs[idx].OpCode != OpCodes.BEFORE_ASYNC_WITH) return false;
        if (stack.Count == 0) return false;

        var cur = SkipTransparent(idx + 1);
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.GET_AWAITABLE) return false;
        cur = SkipTransparent(cur + 1);
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.LOAD_CONST) return false;
        if (!IsNoneConstant(_instrs[cur])) return false;
        cur = SkipTransparent(cur + 1);
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.LEGACY_YIELD_FROM) return false;
        cur = SkipTransparent(cur + 1);
        if (cur >= endIdx || _instrs[cur].OpCode != OpCodes.LEGACY_SETUP_WITH) return false;
        var handlerOffset = _instrs[cur].TargetOffset ?? -1;
        if (!_offsetToIndex.TryGetValue(handlerOffset, out var handlerIdx)) return false;
        var bodyStart = cur + 1;

        ExprNode? target = null;
        bodyStart = SkipTransparent(bodyStart);
        if (bodyStart < handlerIdx &&
            _instrs[bodyStart].OpCode is OpCodes.STORE_FAST or OpCodes.STORE_NAME or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
        {
            var targetName = _instrs[bodyStart].ArgRepr ?? _instrs[bodyStart].ArgVal?.ToString();
            if (string.IsNullOrEmpty(targetName))
            {
                throw new FailFastException("async with target name missing");
            }
            target = new NameExpr(targetName);
            bodyStart++;
        }
        else if (bodyStart < handlerIdx && _instrs[bodyStart].OpCode == OpCodes.POP_TOP)
        {
            bodyStart++;
        }

        if (!TryFindLegacyBlockPop(bodyStart, handlerIdx, out var bodyEnd, preferLast: true))
        {


            for (var p = handlerIdx - 1; p > bodyStart; p--)
            {
                if (_instrs[p].OpCode == OpCodes.LEGACY_POP_BLOCK)
                {
                    bodyEnd = p;
                    break;
                }
            }
            if (bodyEnd < 0) return false;
        }

        var bodyStack = new SymbolicStack();
        var body = DecompileRange(bodyStart, bodyEnd, bodyStack);
        var bodyReturnIdx = -1;
        for (var q = bodyEnd + 1; q < handlerIdx; q++)
        {
            if (_instrs[q].OpCode is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
            {
                bodyReturnIdx = q;
                break;
            }
        }
        var bodyHasReturn = bodyReturnIdx >= 0 && bodyStack.Count > 0;
        if (bodyHasReturn)
        {
            body.Add(new ReturnStmt(bodyStack.Pop()));
        }
        var after = bodyEnd + 1;
        if (bodyHasReturn)
        {
            _handlerStopIndices.Add(handlerIdx);
            after = bodyReturnIdx + 1;
        }
        else
        {


            var endFinallyIdx = after;
            while (endFinallyIdx < endIdx && _instrs[endFinallyIdx].OpCode != OpCodes.LEGACY_END_FINALLY) endFinallyIdx++;
            if (endFinallyIdx < endIdx && _instrs[endFinallyIdx].OpCode == OpCodes.LEGACY_END_FINALLY)
            {
                after = endFinallyIdx + 1;
            }
            else
            {
                var callIdx = bodyEnd + 1;
                while (callIdx < handlerIdx &&
                       !(_instrs[callIdx].OpCode == OpCodes.CALL && (_instrs[callIdx].OpArg ?? 0) is 2 or 3))
                {
                    callIdx++;
                }
                if (callIdx >= handlerIdx) return false;
                after = FindLegacyAsyncExitResume(callIdx, handlerIdx, endIdx);
            }
        }

        var context = stack.Peek();
        stack.Pop();
        var items = new List<WithItem>([new WithItem(context, target)]);
        while (body.Count == 1 && body[0] is WithStmt innerWith && innerWith.IsAsync)
        {
            items.AddRange(innerWith.Items);
            body = innerWith.Body.ToList();
        }
        withStmt = new WithStmt(items, body, IsAsync: true);
        nextIdx = after;
        return true;
    }

    private bool TryDecompileWithStmt(int idx, int endIdx, SymbolicStack stack, out StmtNode withStmt, out int nextIdx)
    {
        withStmt = null!;
        nextIdx = idx;

        if (idx >= _instrs.Count) return false;
        var op = _instrs[idx].OpCode;
        if (op != OpCodes.BEFORE_WITH && op != OpCodes.BEFORE_ASYNC_WITH) return false;
        if (stack.Count == 0) return false;

        var isAsync = op == OpCodes.BEFORE_ASYNC_WITH;
        var ctxExpr = stack.Pop();

        var cur = idx + 1;
        if (isAsync)
        {
            while (cur < _instrs.Count && _instrs[cur].OpCode != OpCodes.END_SEND)
            {
                cur++;
            }
            if (cur < _instrs.Count && _instrs[cur].OpCode == OpCodes.END_SEND)
            {
                cur++;
            }
        }

        ExprNode? targetVar = null;
        while (cur < _instrs.Count && _instrs[cur].OpCode == OpCodes.EXTENDED_ARG) cur++;
        if (cur < _instrs.Count)
        {
            if (_instrs[cur].OpCode is OpCodes.STORE_NAME or OpCodes.STORE_FAST or OpCodes.STORE_GLOBAL or OpCodes.STORE_DEREF)
            {
                var varName = _instrs[cur].ArgRepr ?? _instrs[cur].ArgVal?.ToString() ?? "";
                targetVar = new NameExpr(varName);
                cur++;
            }
            else if (_instrs[cur].OpCode == OpCodes.POP_TOP)
            {
                cur++;
            }
        }

        var scan = cur;
        var withNesting = 0;
        var foundCleanup = false;
        while (scan < _instrs.Count && scan < endIdx)
        {
            if (_instrs[scan].OpCode is OpCodes.BEFORE_WITH or OpCodes.BEFORE_ASYNC_WITH)
            {
                withNesting++;
            }
            else if (!isAsync && _instrs[scan].OpCode == OpCodes.PUSH_EXC_INFO && withNesting == 0 &&
                     scan + 1 < _instrs.Count && _instrs[scan + 1].OpCode == OpCodes.WITH_EXCEPT_START)
            {


                break;
            }
            else if (_instrs[scan].OpCode == OpCodes.LOAD_CONST && _instrs[scan].ArgVal == null &&
                scan + 3 < _instrs.Count && _instrs[scan + 3].OpCode == OpCodes.CALL && (_instrs[scan + 3].OpArg ?? 0) == 2)
            {
                if (withNesting == 0)
                {
                    foundCleanup = true;
                    break;
                }
                withNesting--;
            }
            scan++;
        }
        var bodyEndIdx = scan;


        if (!foundCleanup)
        {
            for (var s = cur; s + 2 < _instrs.Count && s < endIdx; s++)
            {
                if (_instrs[s].OpCode is OpCodes.RAISE_VARARGS or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST or OpCodes.RERAISE &&
                    _instrs[s + 1].OpCode == OpCodes.PUSH_EXC_INFO &&
                    _instrs[s + 2].OpCode == OpCodes.WITH_EXCEPT_START)
                {
                    bodyEndIdx = s + 1;
                    break;
                }
            }
        }

        var withItems = new List<WithItem>([new WithItem(ctxExpr, targetVar)]);
        var bodyStack = new SymbolicStack();
        var withBody = DecompileRange(cur, bodyEndIdx, bodyStack);

        while (withBody.Count == 1 && withBody[0] is WithStmt innerWith && innerWith.IsAsync == isAsync)
        {
            withItems.AddRange(innerWith.Items);
            withBody = innerWith.Body.ToList();
        }


        if (bodyEndIdx < _instrs.Count && _instrs[bodyEndIdx].OpCode == OpCodes.PUSH_EXC_INFO)
        {
            var handlerExitIdx = FindWithHandlerExit(bodyEndIdx);
            if (handlerExitIdx > bodyEndIdx)
            {
                withStmt = new WithStmt(withItems, withBody, isAsync);
                nextIdx = handlerExitIdx;
                return true;
            }
        }

        var cleanupIdx = bodyEndIdx;
        for (var k = 0; k < withItems.Count; k++)
        {
            if (cleanupIdx >= _instrs.Count) break;
            if (_instrs[cleanupIdx].OpCode != OpCodes.LOAD_CONST || _instrs[cleanupIdx].ArgVal != null) break;
            while (cleanupIdx < _instrs.Count)
            {
                if (_instrs[cleanupIdx].OpCode == OpCodes.POP_TOP)
                {
                    cleanupIdx++;
                    break;
                }
                if (_instrs[cleanupIdx].OpCode is OpCodes.PUSH_EXC_INFO or OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                {
                    break;
                }
                cleanupIdx++;
            }
        }

        if (cleanupIdx < _instrs.Count && _instrs[cleanupIdx].OpCode is OpCodes.RETURN_CONST or OpCodes.RETURN_VALUE)
        {
            var retIns = _instrs[cleanupIdx];
            if (retIns.OpCode == OpCodes.RETURN_CONST && retIns.ArgVal != null)
            {
                withBody.Add(new ReturnStmt(new ConstantExpr(retIns.ArgVal)));
                cleanupIdx++;
            }
        }

        withStmt = new WithStmt(withItems, withBody, isAsync);
        nextIdx = cleanupIdx;
        return true;
    }

    private int FindHandlerNormalExit(int hIdx, int endIdx)
    {
        for (var q = hIdx; q < endIdx; q++)
        {
            if (_instrs[q].OpCode != OpCodes.POP_EXCEPT) continue;
            var r = q + 1;
            while (r < endIdx && _instrs[r].OpCode is OpCodes.POP_TOP or OpCodes.NOP or OpCodes.NOT_TAKEN) r++;
            if (r < endIdx && _instrs[r].OpCode is not (OpCodes.RERAISE or OpCodes.COPY))
            {
                return r;
            }
        }
        return -1;
    }

    private int FindWithHandlerExit(int hIdx)
    {
        var hOffset = _instrs[hIdx].Offset;
        var nesting = 0;
        for (var k = hIdx + 1; k < _instrs.Count; k++)
        {
            var op = _instrs[k].OpCode;
            if (op == OpCodes.PUSH_EXC_INFO)
            {
                nesting++;
                continue;
            }
            if (op == OpCodes.POP_EXCEPT)
            {
                if (nesting > 0)
                {
                    nesting--;
                    continue;
                }

                for (var q = k + 1; q < _instrs.Count; q++)
                {
                    var qop = _instrs[q].OpCode;
                    if (qop == OpCodes.JUMP_FORWARD && (_instrs[q].TargetOffset ?? 0) > hOffset)
                    {
                        return _offsetToIndex.TryGetValue(_instrs[q].TargetOffset!.Value, out var ti) ? ti : -1;
                    }
                    if (qop is OpCodes.RETURN_VALUE or OpCodes.RETURN_CONST)
                    {
                        return q + 1;
                    }
                    if (qop is OpCodes.PUSH_EXC_INFO or OpCodes.POP_EXCEPT or OpCodes.RERAISE)
                    {
                        break;
                    }
                }
                return -1;
            }
            if (op == OpCodes.RERAISE && nesting == 0 && (_instrs[k].OpArg ?? 0) <= 1)
            {


                var q = k + 1;
                if (q + 2 < _instrs.Count &&
                    _instrs[q].OpCode == OpCodes.COPY &&
                    _instrs[q + 1].OpCode == OpCodes.POP_EXCEPT &&
                    _instrs[q + 2].OpCode == OpCodes.RERAISE)
                {
                    return q + 3;
                }
                return k + 1;
            }
        }
        return -1;
    }

    private static bool IsAsyncComprehensionExpr(ExprNode expr) => expr switch
    {
        ListCompExpr lc => lc.Clauses.Any(c => c.IsAsync),
        SetCompExpr sc => sc.Clauses.Any(c => c.IsAsync),
        DictCompExpr dc => dc.Clauses.Any(c => c.IsAsync),
        GeneratorExpr ge => ge.Clauses.Any(c => c.IsAsync),
        _ => false
    };

    private bool TryDecompileAwaitOrYieldFrom(int idx, SymbolicStack stack, out ExprNode expr, out int nextIdx)
    {
        expr = null!;
        nextIdx = idx;

        if (idx >= _instrs.Count) return false;
        var op = _instrs[idx].OpCode;
        if (op != OpCodes.GET_AWAITABLE && op != OpCodes.GET_YIELD_FROM_ITER && op != OpCodes.GET_ITER) return false;

        var isAwait = op == OpCodes.GET_AWAITABLE;
        if (stack.Count == 0) return false;


        if ((op == OpCodes.GET_YIELD_FROM_ITER || op == OpCodes.GET_AWAITABLE) &&
            idx + 2 < _instrs.Count &&
            _instrs[idx + 1].OpCode == OpCodes.LOAD_CONST &&
            _instrs[idx + 2].OpCode == OpCodes.LEGACY_YIELD_FROM)
        {
            var legacyTarget = stack.Pop();
            expr = isAwait
                ? (IsAsyncComprehensionExpr(legacyTarget) ? legacyTarget : new AwaitExpr(legacyTarget))
                : new YieldFromExpr(legacyTarget);
            nextIdx = idx + 3;
            return true;
        }


        var cur = idx + 1;
        while (cur < _instrs.Count && _instrs[cur].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN or OpCodes.PUSH_NULL) cur++;
        if (cur >= _instrs.Count || _instrs[cur].OpCode != OpCodes.LOAD_CONST) return false;
        cur++;
        while (cur < _instrs.Count && _instrs[cur].OpCode is OpCodes.NOP or OpCodes.NOT_TAKEN or OpCodes.PUSH_NULL) cur++;
        if (cur >= _instrs.Count || _instrs[cur].OpCode != OpCodes.SEND) return false;
        var sendTargetOffset = _instrs[cur].TargetOffset ?? -1;
        if (!_offsetToIndex.TryGetValue(sendTargetOffset, out var sendEndIdx2) ||
            sendEndIdx2 >= _instrs.Count)
        {
            return false;
        }
        var sendNextIdx = _instrs[sendEndIdx2].OpCode == OpCodes.END_SEND ? sendEndIdx2 + 1 : sendEndIdx2;

        var targetExpr = stack.Pop();
        expr = isAwait
            ? (IsAsyncComprehensionExpr(targetExpr) ? targetExpr : new AwaitExpr(targetExpr))
            : new YieldFromExpr(targetExpr);
        nextIdx = sendNextIdx;
        return true;
    }
}
