using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace OpenSith.Core.Cog;

public enum CogOpCode
{
    Nop,
    Eval,
    Jump,
    JumpIfFalse,
    JumpIfTrue,
    Call,
    Return,
    Sleep,
    Stop
}

public readonly struct CogOp
{
    public CogOpCode Code { get; }
    public int Target { get; }
    public CogExpr? Expr { get; }

    public CogOp(CogOpCode code, int target = -1, CogExpr? expr = null)
    {
        Code = code;
        Target = target;
        Expr = expr;
    }

    public override string ToString() =>
        $"{Code} target={Target} expr={Expr}";
}

public sealed class CogProgram
{
    public List<CogOp> Instructions { get; } = [];
    public Dictionary<string, int> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Compiles a parsed CogScript AST into flat linear bytecode instructions (IR).
/// </summary>
public static class CogCompiler
{
    private sealed class LoopContext
    {
        public List<int> Breaks { get; } = [];
        public List<int> Continues { get; } = [];
    }

    public static CogProgram Compile(CogScript script)
    {
        var program = new CogProgram();
        var instructions = program.Instructions;
        var labels = program.Labels;
        var labelFixups = new List<(int instructionIdx, string labelName, bool isCall)>();
        var loops = new Stack<LoopContext>();

        void CompileStatement(CogStmt stmt)
        {
            switch (stmt)
            {
                case Label lbl:
                    labels[lbl.Name] = instructions.Count;
                    break;

                case ExprStmt exprStmt:
                    if (exprStmt.Expression is CallVerbExpr call && call.VerbName.Equals("sleep", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = call.Arguments.Count > 0 ? call.Arguments[0] : null;
                        instructions.Add(new CogOp(CogOpCode.Sleep, expr: arg));
                    }
                    else
                    {
                        instructions.Add(new CogOp(CogOpCode.Eval, expr: exprStmt.Expression));
                    }
                    break;

                case Block blk:
                    foreach (var s in blk.Statements)
                        CompileStatement(s);
                    break;

                case @If ifStmt:
                {
                    int jmpFalseIdx = instructions.Count;
                    instructions.Add(new CogOp(CogOpCode.JumpIfFalse, target: -1, expr: ifStmt.Condition));

                    CompileStatement(ifStmt.ThenBranch);

                    if (ifStmt.ElseBranch != null)
                    {
                        int jmpEndIdx = instructions.Count;
                        instructions.Add(new CogOp(CogOpCode.Jump, target: -1));

                        int elseStart = instructions.Count;
                        instructions[jmpFalseIdx] = new CogOp(CogOpCode.JumpIfFalse, target: elseStart, expr: ifStmt.Condition);

                        CompileStatement(ifStmt.ElseBranch);

                        int endIdx = instructions.Count;
                        instructions[jmpEndIdx] = new CogOp(CogOpCode.Jump, target: endIdx);
                    }
                    else
                    {
                        int endIdx = instructions.Count;
                        instructions[jmpFalseIdx] = new CogOp(CogOpCode.JumpIfFalse, target: endIdx, expr: ifStmt.Condition);
                    }
                    break;
                }

                case @While whileStmt:
                {
                    int loopStart = instructions.Count;
                    var loopCtx = new LoopContext();
                    loops.Push(loopCtx);

                    int jmpEndIdx = instructions.Count;
                    instructions.Add(new CogOp(CogOpCode.JumpIfFalse, target: -1, expr: whileStmt.Condition));

                    CompileStatement(whileStmt.Body);

                    instructions.Add(new CogOp(CogOpCode.Jump, target: loopStart));
                    int loopEnd = instructions.Count;

                    instructions[jmpEndIdx] = new CogOp(CogOpCode.JumpIfFalse, target: loopEnd, expr: whileStmt.Condition);

                    loops.Pop();

                    foreach (int b in loopCtx.Breaks)
                        instructions[b] = new CogOp(CogOpCode.Jump, target: loopEnd);
                    foreach (int c in loopCtx.Continues)
                        instructions[c] = new CogOp(CogOpCode.Jump, target: loopStart);
                    break;
                }

                case DoWhile doWhileStmt:
                {
                    int loopStart = instructions.Count;
                    var loopCtx = new LoopContext();
                    loops.Push(loopCtx);

                    CompileStatement(doWhileStmt.Body);

                    int condStart = instructions.Count;
                    instructions.Add(new CogOp(CogOpCode.JumpIfTrue, target: loopStart, expr: doWhileStmt.Condition));
                    int loopEnd = instructions.Count;

                    loops.Pop();

                    foreach (int b in loopCtx.Breaks)
                        instructions[b] = new CogOp(CogOpCode.Jump, target: loopEnd);
                    foreach (int c in loopCtx.Continues)
                        instructions[c] = new CogOp(CogOpCode.Jump, target: condStart);
                    break;
                }

                case @For forStmt:
                {
                    if (forStmt.Init != null)
                        CompileStatement(forStmt.Init);

                    int loopStart = instructions.Count;
                    var loopCtx = new LoopContext();
                    loops.Push(loopCtx);

                    int jmpEndIdx = -1;
                    if (forStmt.Condition != null)
                    {
                        jmpEndIdx = instructions.Count;
                        instructions.Add(new CogOp(CogOpCode.JumpIfFalse, target: -1, expr: forStmt.Condition));
                    }

                    CompileStatement(forStmt.Body);

                    int stepStart = instructions.Count;
                    if (forStmt.Step != null)
                    {
                        instructions.Add(new CogOp(CogOpCode.Eval, expr: forStmt.Step));
                    }

                    instructions.Add(new CogOp(CogOpCode.Jump, target: loopStart));
                    int loopEnd = instructions.Count;

                    if (jmpEndIdx >= 0)
                        instructions[jmpEndIdx] = new CogOp(CogOpCode.JumpIfFalse, target: loopEnd, expr: forStmt.Condition);

                    loops.Pop();

                    foreach (int b in loopCtx.Breaks)
                        instructions[b] = new CogOp(CogOpCode.Jump, target: loopEnd);
                    foreach (int c in loopCtx.Continues)
                        instructions[c] = new CogOp(CogOpCode.Jump, target: stepStart);
                    break;
                }

                case @Break:
                    if (loops.Count > 0)
                    {
                        int idx = instructions.Count;
                        instructions.Add(new CogOp(CogOpCode.Jump, target: -1));
                        loops.Peek().Breaks.Add(idx);
                    }
                    break;

                case @Continue:
                    if (loops.Count > 0)
                    {
                        int idx = instructions.Count;
                        instructions.Add(new CogOp(CogOpCode.Jump, target: -1));
                        loops.Peek().Continues.Add(idx);
                    }
                    break;

                case CallLabel callLabel:
                {
                    int idx = instructions.Count;
                    instructions.Add(new CogOp(CogOpCode.Call, target: -1));
                    labelFixups.Add((idx, callLabel.Label, true));
                    break;
                }

                case @Goto gotoStmt:
                {
                    int idx = instructions.Count;
                    instructions.Add(new CogOp(CogOpCode.Jump, target: -1));
                    labelFixups.Add((idx, gotoStmt.Label, false));
                    break;
                }

                case @Return ret:
                    instructions.Add(new CogOp(CogOpCode.Return, expr: ret.Value));
                    break;
            }
        }

        foreach (var s in script.Statements)
            CompileStatement(s);

        // Implicit return at script end
        instructions.Add(new CogOp(CogOpCode.Return));

        // Resolve label fixups
        foreach (var (idx, labelName, isCall) in labelFixups)
        {
            if (labels.TryGetValue(labelName, out int target))
            {
                instructions[idx] = new CogOp(isCall ? CogOpCode.Call : CogOpCode.Jump, target: target);
            }
            else
            {
                instructions[idx] = new CogOp(isCall ? CogOpCode.Call : CogOpCode.Jump, target: instructions.Count - 1);
            }
        }

        return program;
    }
}

/// <summary>
/// Execution frame for a running or suspended COG script continuation.
/// </summary>
public sealed class CogExecutionContext
{
    public CogInstance Instance { get; }
    public int Pc { get; set; }
    public Stack<int> CallStack { get; } = new();
    public int SenderRef { get; }
    public int SenderId { get; }
    public int SourceRef { get; }
    public CogValue[] Parameters { get; }
    public CogValue ReturnValue { get; set; } = CogValue.Null;
    public bool Terminated { get; set; }

    public CogExecutionContext(
        CogInstance instance,
        int pc,
        int senderRef = -1,
        CogValue[]? parameters = null,
        int senderId = -1,
        int sourceRef = -1)
    {
        Instance = instance;
        Pc = pc;
        SenderRef = senderRef;
        SenderId = senderId;
        SourceRef = sourceRef;
        Parameters = parameters ?? [];
    }
}

/// <summary>
/// Jedi Knight COG Virtual Machine.
/// Dispatches engine messages, executes AST statement flows, manages coroutine sleep states,
/// and routes verb calls to the world context.
/// </summary>
public sealed class CogVm
{
    private const int MaxStepsPerExecution = 100_000;
    private const int MaxRecursionDepth = 64;

    private sealed class ExtendedTimer
    {
        public int TimerId { get; init; }
        public float TimeLeft { get; set; }
        public CogValue Param0 { get; init; }
        public CogValue Param1 { get; init; }
    }

    private readonly ICogWorldContext _context;
    private readonly List<CogInstance> _instances = [];
    private readonly Dictionary<int, CogInstance> _instancesById = new();
    private readonly Dictionary<CogInstance, List<ExtendedTimer>> _extendedTimers = new();
    private int _recursionDepth;
    private int _masterCogId = -1;

    public ICogWorldContext Context => _context;
    public IReadOnlyList<CogInstance> Instances => _instances;
    public int MasterCogId => _masterCogId;

    public CogVm(ICogWorldContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void AddInstance(CogInstance instance)
    {
        _instances.Add(instance);
        if (instance.CogId >= 0)
            _instancesById[instance.CogId] = instance;
    }

    public CogValue SendMessage(int cogId, string message, int senderRef = -1, params CogValue[]? parameters)
        => SendMessage(cogId, message, senderRef, -1, -1, parameters);

    public CogValue SendMessage(int cogId, string message, int senderRef, int senderId, int sourceRef, params CogValue[]? parameters)
    {
        if (_instancesById.TryGetValue(cogId, out var instance))
            return SendMessage(instance, message, senderRef, senderId, sourceRef, parameters);

        _context.SendMessage(cogId, message);
        return CogValue.Null;
    }

    public CogValue SendMessage(CogInstance instance, string message, int senderRef = -1, params CogValue[]? parameters)
        => SendMessage(instance, message, senderRef, -1, -1, parameters);

    public CogValue SendMessage(CogInstance instance, string message, int senderRef, int senderId, int sourceRef, params CogValue[]? parameters)
    {
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        if (string.IsNullOrWhiteSpace(message)) return CogValue.Null;

        if (_recursionDepth >= MaxRecursionDepth)
            throw new InvalidOperationException($"COG VM exceeded maximum message recursion depth of {MaxRecursionDepth}.");

        if (!TryResolveMessageLabel(instance, message, out int labelPc))
            return CogValue.Null;

        var context = new CogExecutionContext(instance, labelPc, senderRef, parameters, senderId, sourceRef);

        _recursionDepth++;
        try
        {
            return Execute(context);
        }
        finally
        {
            _recursionDepth--;
        }
    }

    public void Update(float dt)
    {
        if (dt <= 0f) return;

        var list = _instances.ToArray();
        foreach (var instance in list)
        {
            // Snapshot existing extended timers before sleep wakeup so newly created timers are not decremented on the wakeup tick.
            ExtendedTimer[]? existingTimers = _extendedTimers.TryGetValue(instance, out var extList) && extList.Count > 0
                ? extList.ToArray()
                : null;

            // 1. Coroutine sleep resumption
            if (instance.IsSleeping && instance.SuspendedContext != null)
            {
                instance.SleepTimeLeft -= dt;
                if (instance.SleepTimeLeft <= 0f)
                {
                    instance.IsSleeping = false;
                    var ctx = instance.SuspendedContext;
                    instance.SuspendedContext = null;
                    Execute(ctx);
                }
            }

            // 1b. WaitForStop resumption
            if (instance.WaitingForStopThingId >= 0 && instance.SuspendedContext != null)
            {
                if (!_context.IsThingMoving(instance.WaitingForStopThingId))
                {
                    instance.WaitingForStopThingId = -1;
                    var ctx = instance.SuspendedContext;
                    instance.SuspendedContext = null;
                    Execute(ctx);
                }
            }

            // 2. Timer
            if (instance.TimerActive)
            {
                instance.TimerTimeLeft -= dt;
                if (instance.TimerTimeLeft <= 0f)
                {
                    instance.TimerActive = false;
                    instance.TimerTimeLeft = -1f;
                    SendMessage(instance, "timer");
                }
            }

            // 2b. Extended multi-ID timers (SetTimerEx)
            if (existingTimers != null && _extendedTimers.TryGetValue(instance, out var currentExtList))
            {
                foreach (var timer in existingTimers)
                {
                    if (!currentExtList.Contains(timer))
                        continue;

                    timer.TimeLeft -= dt;
                    if (timer.TimeLeft <= 0f)
                    {
                        currentExtList.Remove(timer);
                        SendMessage(instance, "timer", senderRef: -1, senderId: timer.TimerId, sourceRef: -1, timer.Param0, timer.Param1);
                    }
                }
            }

            // 3. Pulse
            if (instance.PulseActive && instance.PulseInterval > 0f)
            {
                instance.PulseTimeLeft -= dt;
                if (instance.PulseTimeLeft <= 0f)
                {
                    instance.PulseTimeLeft += instance.PulseInterval;
                    SendMessage(instance, "pulse");
                }
            }
        }
    }

    private CogValue Execute(CogExecutionContext context)
    {
        var instructions = context.Instance.Program.Instructions;
        var instance = context.Instance;
        int stepCount = 0;

        while (context.Pc >= 0 && context.Pc < instructions.Count && !context.Terminated)
        {
            if (++stepCount > MaxStepsPerExecution)
                throw new InvalidOperationException($"COG VM exceeded maximum instruction step limit of {MaxStepsPerExecution} (possible infinite loop in script '{instance.Script.Name}').");

            var op = instructions[context.Pc];
            switch (op.Code)
            {
                case CogOpCode.Nop:
                    context.Pc++;
                    break;

                case CogOpCode.Eval:
                    if (op.Expr != null)
                    {
                        EvaluateExpr(op.Expr, context);
                    }
                    context.Pc++;
                    if (instance.WaitingForStopThingId >= 0 && instance.SuspendedContext == context)
                        return context.ReturnValue;
                    break;

                case CogOpCode.Jump:
                    context.Pc = op.Target;
                    break;

                case CogOpCode.JumpIfFalse:
                    if (op.Expr != null && !EvaluateExpr(op.Expr, context).AsBool)
                        context.Pc = op.Target;
                    else
                        context.Pc++;
                    break;

                case CogOpCode.JumpIfTrue:
                    if (op.Expr != null && EvaluateExpr(op.Expr, context).AsBool)
                        context.Pc = op.Target;
                    else
                        context.Pc++;
                    break;

                case CogOpCode.Call:
                    if (context.CallStack.Count >= 128)
                        throw new InvalidOperationException($"COG VM exceeded maximum call stack depth of 128 in script '{instance.Script.Name}'.");
                    context.CallStack.Push(context.Pc + 1);
                    context.Pc = op.Target;
                    break;

                case CogOpCode.Return:
                    if (op.Expr != null)
                        context.ReturnValue = EvaluateExpr(op.Expr, context);
                    if (context.CallStack.Count > 0)
                        context.Pc = context.CallStack.Pop();
                    else
                        context.Terminated = true;
                    break;

                case CogOpCode.Sleep:
                {
                    float duration = op.Expr != null ? EvaluateExpr(op.Expr, context).AsFloat : 0f;
                    context.Pc++;
                    instance.IsSleeping = true;
                    instance.SleepTimeLeft = duration;
                    instance.SuspendedContext = context;
                    return context.ReturnValue;
                }

                case CogOpCode.Stop:
                    context.Terminated = true;
                    break;
            }
        }

        context.Terminated = true;
        return context.ReturnValue;
    }

    private CogValue EvaluateExpr(CogExpr expr, CogExecutionContext context)
    {
        var instance = context.Instance;

        switch (expr)
        {
            case IntLiteral i:
                return new CogValue(i.Value);

            case FloatLiteral f:
                return new CogValue(f.Value);

            case StringLiteral s:
                return new CogValue(s.Value);

            case VectorLiteral v:
                return new CogValue(v.Value);

            case Identifier id:
                if (id.Name.Equals("true", StringComparison.OrdinalIgnoreCase)) return new CogValue(1);
                if (id.Name.Equals("false", StringComparison.OrdinalIgnoreCase)) return new CogValue(0);
                {
                    var symVal = instance.GetSymbolValue(id.Name);
                    if (symVal.Type == CogValueType.Int && symVal.AsInt == -1 &&
                        instance.SymbolSlots.TryGetValue(id.Name, out int slot))
                    {
                        var sym = instance.Script.Symbols[slot];
                        if (sym.Type == CogSymbolType.Template && !string.IsNullOrEmpty(sym.DefaultValue))
                            return new CogValue(sym.DefaultValue);
                    }
                    return symVal;
                }

            case IndexExpr idxExpr:
            {
                int index = EvaluateExpr(idxExpr.Index, context).AsInt;
                if (idxExpr.Target is Identifier targetId)
                    return instance.GetArrayElement(targetId.Name, index);

                var targetVal = EvaluateExpr(idxExpr.Target, context);
                if (targetVal.Type == CogValueType.Vector)
                {
                    var vec = targetVal.AsVector3;
                    return index switch
                    {
                        0 => new CogValue(vec.X),
                        1 => new CogValue(vec.Y),
                        2 => new CogValue(vec.Z),
                        _ => CogValue.Null
                    };
                }
                return CogValue.Null;
            }

            case AssignExpr assign:
            {
                var val = EvaluateExpr(assign.Value, context);
                if (assign.Target is Identifier targetId)
                {
                    instance.SetSymbolValue(targetId.Name, val);
                }
                else if (assign.Target is IndexExpr targetIdx && targetIdx.Target is Identifier arrayId)
                {
                    int index = EvaluateExpr(targetIdx.Index, context).AsInt;
                    instance.SetArrayElement(arrayId.Name, index, val);
                }
                return val;
            }

            case BinaryExpr bin:
            {
                if (bin.Op == CogBinaryOp.LogicalAnd)
                {
                    var left = EvaluateExpr(bin.Left, context);
                    if (!left.AsBool) return new CogValue(0);
                    var right = EvaluateExpr(bin.Right, context);
                    return new CogValue(right.AsBool ? 1 : 0);
                }
                if (bin.Op == CogBinaryOp.LogicalOr)
                {
                    var left = EvaluateExpr(bin.Left, context);
                    if (left.AsBool) return new CogValue(1);
                    var right = EvaluateExpr(bin.Right, context);
                    return new CogValue(right.AsBool ? 1 : 0);
                }

                var l = EvaluateExpr(bin.Left, context);
                var r = EvaluateExpr(bin.Right, context);

                return bin.Op switch
                {
                    CogBinaryOp.Add => l + r,
                    CogBinaryOp.Subtract => l - r,
                    CogBinaryOp.Multiply => l * r,
                    CogBinaryOp.Divide => l / r,
                    CogBinaryOp.Modulo => l % r,
                    CogBinaryOp.BitwiseAnd => l & r,
                    CogBinaryOp.BitwiseOr => l | r,
                    CogBinaryOp.BitwiseXor => l ^ r,
                    CogBinaryOp.Equal => new CogValue(l == r ? 1 : 0),
                    CogBinaryOp.NotEqual => new CogValue(l != r ? 1 : 0),
                    CogBinaryOp.LessThan => new CogValue(l < r ? 1 : 0),
                    CogBinaryOp.LessThanOrEqual => new CogValue(l <= r ? 1 : 0),
                    CogBinaryOp.GreaterThan => new CogValue(l > r ? 1 : 0),
                    CogBinaryOp.GreaterThanOrEqual => new CogValue(l >= r ? 1 : 0),
                    _ => CogValue.Null
                };
            }

            case UnaryExpr un:
            {
                var operand = EvaluateExpr(un.Operand, context);
                return un.Op switch
                {
                    CogUnaryOp.Negate => -operand,
                    CogUnaryOp.Not => !operand,
                    CogUnaryOp.BitwiseNot => ~operand,
                    CogUnaryOp.Plus => operand,
                    _ => operand
                };
            }

            case CallVerbExpr call:
                return EvaluateVerb(call, context);

            default:
                return CogValue.Null;
        }
    }

    private static CogValue Arg(List<CogValue> args, int idx) => idx < args.Count ? args[idx] : CogValue.Null;

    private CogValue EvaluateVerb(CallVerbExpr call, CogExecutionContext context)
    {
        var args = new List<CogValue>(call.Arguments.Count);
        foreach (var a in call.Arguments)
            args.Add(EvaluateExpr(a, context));

        string verb = call.VerbName.ToLowerInvariant();

        if (TryEvaluateWorldAndVmVerb(verb, call, args, context, out var result))
            return result;
        if (TryEvaluateThingVerb(verb, args, context.Instance.CogId, out result))
            return result;
        if (TryEvaluateInventoryAndAiVerb(verb, args, out result))
            return result;
        if (TryEvaluateEffectsJkAndMathVerb(verb, args, out result))
            return result;

        // Tolerant fallback for unrecognized verbs
        return CogValue.Null;
    }

    private bool TryEvaluateWorldAndVmVerb(string verb, CallVerbExpr call, List<CogValue> args, CogExecutionContext context, out CogValue result)
    {
        var instance = context.Instance;
        CogValue Arg(int idx) => CogVm.Arg(args, idx);
        switch (verb)
        {
            // World query verbs
            case "getthingsector":
                result = new CogValue(_context.GetThingSector(Arg(0).AsInt));
                return true;

            case "getthingpos":
                result = new CogValue(_context.GetThingPos(Arg(0).AsInt));
                return true;

            case "movetoframe":
                _context.MoveToFrame(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "isthingmoving":
            case "ismoving":
                result = new CogValue(_context.IsThingMoving(Arg(0).AsInt) ? 1 : 0);
                return true;

            case "waitforstop":
            {
                int thingId = Arg(0).AsInt;
                if (thingId >= 0 && _context.IsThingMoving(thingId))
                {
                    instance.WaitingForStopThingId = thingId;
                    instance.SuspendedContext = context;
                }
                result = CogValue.Null;
                return true;
            }

            case "getcurframe":
                result = new CogValue(_context.GetCurFrame(Arg(0).AsInt));
                return true;

            case "getlocalplayerthing":
            case "jkgetlocalplayer":
                result = new CogValue(_context.GetLocalPlayerThing());
                return true;

            case "setsectoradjoins":
            case "sectoradjoins":
                _context.SetSectorAdjoins(Arg(0).AsInt, Arg(1).AsBool);
                result = CogValue.Null;
                return true;

            case "setsectorlight":
            case "sectorlight":
                _context.SetSectorLight(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "sectorthrust":
                _context.SectorThrust(Arg(0).AsInt, Arg(1).AsVector3, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "playsoundthing":
                result = new CogValue(_context.PlaySoundThing(Arg(0).AsString, Arg(1).AsInt, Arg(2).AsFloat, Arg(3).AsFloat, Arg(4).AsFloat, Arg(5).AsInt));
                return true;

            // Engine / communication verbs
            case "sendmessage":
            {
                int cogId = Arg(0).AsInt;
                string msg = ResolveMessageName(call, args, 1);
                result = SendMessage(cogId, msg, instance.CogId);
                return true;
            }

            case "sendmessageex":
            {
                int cogId = Arg(0).AsInt;
                string msg = ResolveMessageName(call, args, 1);
                result = SendMessage(cogId, msg, instance.CogId, [Arg(2), Arg(3), Arg(4), Arg(5)]);
                return true;
            }

            case "getselfcog":
                result = new CogValue(instance.CogId);
                return true;

            case "setmastercog":
                _masterCogId = Arg(0).AsInt;
                result = CogValue.Null;
                return true;

            case "getmastercog":
                result = new CogValue(_masterCogId);
                return true;

            case "print":
                _context.Print(Arg(0).AsString);
                result = CogValue.Null;
                return true;

            case "printint":
                _context.Print(Arg(0).AsInt.ToString(CultureInfo.InvariantCulture));
                result = CogValue.Null;
                return true;

            case "printflex":
                _context.Print(Arg(0).AsFloat.ToString("0.######", CultureInfo.InvariantCulture));
                result = CogValue.Null;
                return true;

            case "rand":
                result = new CogValue(_context.Rand());
                return true;

            // VM execution / context verbs
            case "getsenderref":
                result = new CogValue(context.SenderRef);
                return true;

            case "getsenderid":
                result = new CogValue(ResolveSenderId(context));
                return true;

            case "getsourceref":
                result = new CogValue(ResolveSourceRef(context));
                return true;

            case "getleveltime":
                result = new CogValue(_context.GetLevelTime());
                return true;

            case "getparam":
            {
                int pIdx = Arg(0).AsInt;
                result = (pIdx >= 0 && pIdx < context.Parameters.Length) ? context.Parameters[pIdx] : CogValue.Null;
                return true;
            }

            case "settimer":
            {
                float t = Arg(0).AsFloat;
                instance.TimerTimeLeft = t;
                instance.TimerActive = t > 0f;
                result = new CogValue(t);
                return true;
            }

            case "setpulse":
            {
                float p = Arg(0).AsFloat;
                instance.PulseInterval = p;
                instance.PulseTimeLeft = p;
                instance.PulseActive = p > 0f;
                result = new CogValue(p);
                return true;
            }

            case "settimerex":
            {
                float t = Arg(0).AsFloat;
                if (args.Count >= 2)
                {
                    int timerId = Arg(1).AsInt;
                    if (!_extendedTimers.TryGetValue(instance, out var list))
                    {
                        list = [];
                        _extendedTimers[instance] = list;
                    }
                    list.RemoveAll(x => x.TimerId == timerId);
                    if (t > 0f)
                    {
                        list.Add(new ExtendedTimer
                        {
                            TimerId = timerId,
                            TimeLeft = t,
                            Param0 = Arg(2),
                            Param1 = Arg(3)
                        });
                    }
                    result = new CogValue(t);
                    return true;
                }

                instance.TimerTimeLeft = t;
                instance.TimerActive = t > 0f;
                result = new CogValue(t);
                return true;
            }

            case "killtimerex":
            {
                if (args.Count >= 1 && _extendedTimers.TryGetValue(instance, out var list))
                {
                    int timerId = Arg(0).AsInt;
                    list.RemoveAll(x => x.TimerId == timerId);
                }
                else
                {
                    _extendedTimers.Remove(instance);
                    instance.TimerActive = false;
                    instance.TimerTimeLeft = -1f;
                }
                result = CogValue.Null;
                return true;
            }

            case "sleep":
            {
                float s = Arg(0).AsFloat;
                instance.IsSleeping = true;
                instance.SleepTimeLeft = s;
                instance.SuspendedContext = context;
                result = CogValue.Null;
                return true;
            }

            case "returnex":
            {
                var ret = Arg(0);
                context.ReturnValue = ret;
                int nextPc = context.Pc + 1;
                var instructions = instance.Program.Instructions;
                if (nextPc < instructions.Count &&
                    instructions[nextPc].Code == CogOpCode.Eval &&
                    instructions[nextPc].Expr is CallVerbExpr nextCall &&
                    nextCall.VerbName.Equals("returnex", StringComparison.OrdinalIgnoreCase))
                {
                    context.CallStack.Clear();
                    context.Terminated = true;
                }
                result = ret;
                return true;
            }

            case "stop":
            {
                context.CallStack.Clear();
                context.Terminated = true;
                result = CogValue.Null;
                return true;
            }

            default:
                result = CogValue.Null;
                return false;
        }
    }

    private bool TryEvaluateThingVerb(string verb, List<CogValue> args, int cogId, out CogValue result)
    {
        CogValue Arg(int idx) => CogVm.Arg(args, idx);
        switch (verb)
        {

            // Thing lifecycle & physics verbs
            case "creatething":
                result = new CogValue(_context.CreateThing(Arg(0).AsString, Arg(1).AsInt));
                return true;

            case "createthingnr":
                result = new CogValue(_context.CreateThingNR(Arg(0).AsString, Arg(1).AsInt));
                return true;

            case "createthingatpos":
                result = new CogValue(_context.CreateThingAtPos(Arg(0).AsString, Arg(1).AsInt, Arg(2).AsVector3, Arg(3).AsVector3));
                return true;

            case "createthingatposnr":
                result = new CogValue(_context.CreateThingAtPosNR(Arg(0).AsString, Arg(1).AsInt, Arg(2).AsVector3, Arg(3).AsVector3));
                return true;

            case "destroything":
                _context.DestroyThing(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "setlifeleft":
                _context.SetLifeleft(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "getlifeleft":
                result = new CogValue(_context.GetLifeLeft(Arg(0).AsInt));
                return true;

            case "damagething":
                result = new CogValue(_context.DamageThing(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsInt, Arg(3).AsInt));
                return true;

            case "healthing":
                _context.HealThing(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "getthinghealth":
            case "gethealth":
                result = new CogValue(_context.GetThingHealth(Arg(0).AsInt));
                return true;

            case "setthinghealth":
                _context.SetThingHealth(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "getthingmaxhealth":
                result = new CogValue(_context.GetThingMaxHealth(Arg(0).AsInt));
                return true;

            case "getthingtemplate":
                result = new CogValue(_context.GetThingTemplate(Arg(0).AsInt));
                return true;

            case "setthingvel":
                _context.SetThingVel(Arg(0).AsInt, Arg(1).AsVector3);
                result = CogValue.Null;
                return true;

            case "addthingvel":
                _context.AddThingVel(Arg(0).AsInt, Arg(1).AsVector3);
                result = CogValue.Null;
                return true;

            case "getthingvel":
                result = new CogValue(_context.GetThingVel(Arg(0).AsInt));
                return true;

            case "setthingrotvel":
                _context.SetThingRotVel(Arg(0).AsInt, Arg(1).AsVector3);
                result = CogValue.Null;
                return true;

            case "getthingrotvel":
                result = new CogValue(_context.GetThingRotVel(Arg(0).AsInt));
                return true;

            case "applyforce":
                _context.ApplyForce(Arg(0).AsInt, Arg(1).AsVector3);
                result = CogValue.Null;
                return true;

            case "stopthing":
                _context.StopThing(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "thinglight":
            case "setthinglight":
                _context.ThingLight(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "getthinglight":
                result = new CogValue(_context.GetThingLight(Arg(0).AsInt));
                return true;

            case "playkey":
                result = new CogValue(_context.PlayKey(Arg(0).AsInt, Arg(1).AsString, Arg(2).AsInt, Arg(3).AsInt));
                return true;

            case "stopkey":
                _context.StopKey(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "playmode":
                result = new CogValue(_context.PlayMode(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "getthingflags":
                result = new CogValue(_context.GetThingFlags(Arg(0).AsInt));
                return true;

            case "setthingflags":
                _context.SetThingFlags(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "clearthingflags":
                _context.ClearThingFlags(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "getactorflags":
                result = new CogValue(_context.GetActorFlags(Arg(0).AsInt));
                return true;

            case "setactorflags":
                _context.SetActorFlags(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "clearactorflags":
                _context.ClearActorFlags(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "getphysicsflags":
                result = new CogValue(_context.GetPhysicsFlags(Arg(0).AsInt));
                return true;

            case "setphysicsflags":
                _context.SetPhysicsFlags(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "clearphysicsflags":
                _context.ClearPhysicsFlags(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "getitemflags":
                result = new CogValue(_context.GetItemFlags(Arg(0).AsInt));
                return true;

            case "getattachflags":
                result = new CogValue(_context.GetAttachFlags(Arg(0).AsInt));
                return true;

            case "attachthingtothingex":
                _context.AttachThingToThingEx(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "detachthing":
                _context.DetachThing(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "getthingclasscog":
                result = new CogValue(_context.GetThingClassCog(Arg(0).AsInt));
                return true;

            case "getthinggeomode":
                result = new CogValue(_context.GetThingGeoMode(Arg(0).AsInt));
                return true;

            case "setthingcurgeomode":
                _context.SetThingCurGeoMode(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "getthingparent":
                result = new CogValue(_context.GetThingParent(Arg(0).AsInt));
                return true;

            case "getthingsignature":
                result = new CogValue(_context.GetThingSignature(Arg(0).AsInt));
                return true;

            case "getthingthrust":
                result = new CogValue(_context.GetThingThrust(Arg(0).AsInt));
                return true;

            case "getthingtype":
                result = new CogValue(_context.GetThingType(Arg(0).AsInt));
                return true;

            case "getthinguserdata":
                result = new CogValue(_context.GetThingUserData(Arg(0).AsInt));
                return true;

            case "setthinguserdata":
                _context.SetThingUserData(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "setthinglook":
                _context.SetThingLook(Arg(0).AsInt, Arg(1).AsVector3);
                result = CogValue.Null;
                return true;

            case "getthinglvec":
                result = new CogValue(_context.GetThingLVec(Arg(0).AsInt));
                return true;

            case "capturething":
                _context.CaptureThing(Arg(0).AsInt, cogId);
                result = CogValue.Null;
                return true;

            case "setthingpulse":
                _context.SetThingPulse(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "setthingcollidesize":
                _context.SetThingCollideSize(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "setcollidetype":
                _context.SetCollideType(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "setactorextraspeed":
                _context.SetActorExtraSpeed(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "setactorweapon":
                _context.SetActorWeapon(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsString);
                result = CogValue.Null;
                return true;

            case "setarmedmode":
                _context.SetArmedMode(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "setparticlegrowthspeed":
                _context.SetParticleGrowthSpeed(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "parsearg":
                _context.ParseArg(Arg(0).AsInt, Arg(1).AsString);
                result = CogValue.Null;
                return true;

            case "getcursector":
                result = args.Count > 0
                    ? new CogValue(_context.GetThingSector(Arg(0).AsInt))
                    : new CogValue(_context.GetCurSector());
                return true;

            default:
                result = CogValue.Null;
                return false;
        }
    }

    private bool TryEvaluateInventoryAndAiVerb(string verb, List<CogValue> args, out CogValue result)
    {
        CogValue Arg(int idx) => CogVm.Arg(args, idx);
        switch (verb)
        {
            // Inventory & goal verbs
            case "changeinv":
                result = new CogValue(_context.ChangeInv(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsFloat));
                return true;

            case "getinv":
                result = new CogValue(_context.GetInv(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "setinv":
                _context.SetInv(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "getinvmax":
                result = new CogValue(_context.GetInvMax(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "setgoalflags":
                _context.SetGoalFlags(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "cleargoalflags":
                _context.ClearGoalFlags(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "getgoalflags":
                result = new CogValue(_context.GetGoalFlags(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "takeitem":
                _context.TakeItem(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "adddynamictint":
                _context.AddDynamicTint(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsFloat, Arg(3).AsFloat);
                result = CogValue.Null;
                return true;

            case "adddynamicadd":
                _context.AddDynamicAdd(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsFloat, Arg(3).AsFloat);
                result = CogValue.Null;
                return true;

            case "getautoreload":
                result = new CogValue(_context.GetAutoReload());
                return true;

            case "getautopickup":
                result = new CogValue(_context.GetAutoPickup());
                return true;

            case "getautoswitch":
                result = new CogValue(_context.GetAutoSwitch());
                return true;

            case "autoselectweapon":
                result = new CogValue(_context.AutoSelectWeapon(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "selectweapon":
                _context.SelectWeapon(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "getcurweapon":
                result = new CogValue(_context.GetCurWeapon(Arg(0).AsInt));
                return true;

            case "getcurweaponmode":
                result = new CogValue(_context.GetCurWeaponMode());
                return true;

            case "getweaponpriority":
                result = new CogValue(_context.GetWeaponPriority(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt));
                return true;

            case "setweaponflags":
                _context.SetWeaponFlags(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "changefirerate":
                _context.ChangeFireRate(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "createbackpack":
                result = new CogValue(_context.CreateBackpack(Arg(0).AsInt));
                return true;

            case "pickupbackpack":
                _context.PickupBackpack(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "setcurinvweapon":
                _context.SetCurInvWeapon(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "getcurinvweapon":
                result = new CogValue(_context.GetCurInvWeapon(Arg(0).AsInt));
                return true;

            case "setcurweapon":
                _context.SetCurWeapon(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "activatebin":
                _context.ActivateBin(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "deactivatebin":
                result = new CogValue(_context.DeactivateBin(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "isinvactivated":
                result = new CogValue(_context.IsInvActivated(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "setinvactivated":
                _context.SetInvActivated(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "setinvavailable":
                _context.SetInvAvailable(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "isinvavailable":
                result = new CogValue(_context.IsInvAvailable(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "setbinwait":
                _context.SetBinWait(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "activateweapon":
                _context.ActivateWeapon(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "deactivateweapon":
                result = new CogValue(_context.DeactivateWeapon(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "fireprojectile":
                result = new CogValue(_context.FireProjectile(
                    Arg(0).AsInt,
                    Arg(1).AsString,
                    Arg(2).AsString,
                    Arg(3).AsInt,
                    Arg(4).AsVector3,
                    Arg(5).AsVector3,
                    Arg(6).AsFloat,
                    Arg(7).AsInt,
                    Arg(8).AsFloat,
                    Arg(9).AsFloat));
                return true;

            case "setfirewait":
                _context.SetFireWait(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "setmountwait":
                _context.SetMountWait(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            // AI, targeting & joint verbs
            case "amputatejoint":
                _context.AmputateJoint(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "aisetclass":
                _context.AISetClass(Arg(0).AsInt, Arg(1).AsString);
                result = CogValue.Null;
                return true;

            case "aiflee":
                _context.AIFlee(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "aisetlookframe":
                _context.AISetLookFrame(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "aisetmoveframe":
                _context.AISetMoveFrame(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "aisetlookpos":
                _context.AISetLookPos(Arg(0).AsInt, Arg(1).AsVector3);
                result = CogValue.Null;
                return true;

            case "aisetmovepos":
                _context.AISetMovePos(Arg(0).AsInt, Arg(1).AsVector3);
                result = CogValue.Null;
                return true;

            case "aisetmovespeed":
                _context.AISetMoveSpeed(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "aijump":
                _context.AIJump(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "aigetmode":
                result = new CogValue(_context.AiGetMode(Arg(0).AsInt));
                return true;

            case "aisetmode":
                _context.AiSetMode(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "aiclearmode":
                _context.AiClearMode(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "getactorweapon":
                result = new CogValue(_context.GetActorWeapon(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "skilltarget":
            {
                int targetThing = Arg(0).AsInt;
                int sourceThing = Arg(1).AsInt;
                float param0 = Arg(2).AsFloat;
                float param1 = Arg(3).AsFloat;
                int classCog = _context.GetThingClassCog(targetThing);
                if (classCog >= 0 && _instancesById.ContainsKey(classCog))
                {
                    result = SendMessage(classCog, "skill", targetThing, -1, sourceThing, new CogValue(param0), new CogValue(param1));
                    return true;
                }
                result = new CogValue(_context.SkillTarget(targetThing, sourceThing, param0, param1));
                return true;
            }

            case "firstthinginview":
                result = new CogValue(_context.FirstThingInView(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsFloat, Arg(3).AsInt));
                return true;

            case "nextthinginview":
                result = new CogValue(_context.NextThingInView());
                return true;

            case "haslos":
                result = new CogValue(_context.HasLOS(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            case "thingviewdot":
                result = new CogValue(_context.ThingViewDot(Arg(0).AsInt, Arg(1).AsInt));
                return true;

            default:
                result = CogValue.Null;
                return false;
        }
    }

    private bool TryEvaluateEffectsJkAndMathVerb(string verb, List<CogValue> args, out CogValue result)
    {
        CogValue Arg(int idx) => CogVm.Arg(args, idx);
        switch (verb)
        {
            // Surface & audio verbs
            case "setsurfacecel":
            case "setwallcel":
                _context.SetSurfaceCel(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "getsurfacecel":
            case "getwallcel":
                result = new CogValue(_context.GetSurfaceCel(Arg(0).AsInt));
                return true;

            case "slidesurface":
            case "slidewall":
                _context.SlideSurface(Arg(0).AsInt, Arg(1).AsVector3, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "slideceilingsky":
                _context.SlideCeilingSky(Arg(0).AsFloat, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "surfaceanim":
                result = new CogValue(_context.SurfaceAnim(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsInt));
                return true;

            case "stopsurfaceanim":
                _context.StopSurfaceAnim(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "materialanim":
                result = new CogValue(_context.MaterialAnim(Arg(0).AsString, Arg(1).AsFloat, Arg(2).AsInt));
                return true;

            case "surfacecenter":
            case "getsurfacecenter":
                result = new CogValue(_context.SurfaceCenter(Arg(0).AsInt));
                return true;

            case "playsoundlocal":
                result = new CogValue(_context.PlaySoundLocal(Arg(0).AsString, Arg(1).AsFloat, Arg(2).AsFloat, Arg(3).AsInt));
                return true;

            case "playsoundpos":
                result = new CogValue(_context.PlaySoundPos(Arg(0).AsString, Arg(1).AsVector3, Arg(2).AsFloat, Arg(3).AsFloat, Arg(4).AsFloat, Arg(5).AsInt));
                return true;

            case "stopsound":
                _context.StopSound(Arg(0).AsInt, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "changesoundpitch":
                _context.ChangeSoundPitch(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "changesoundvol":
                _context.ChangeSoundVol(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "getsoundlen":
                result = new CogValue(_context.GetSoundLen(Arg(0).AsString));
                return true;

            case "getkeylen":
                result = new CogValue(_context.GetKeyLen(Arg(0).AsString));
                return true;

            case "cyclecamera":
                _context.CycleCamera();
                result = CogValue.Null;
                return true;

            case "getcurrentcamera":
                result = new CogValue(_context.GetCurrentCamera());
                return true;

            case "setcurrentcamera":
                _context.SetCurrentCamera(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "setcamerafocus":
                _context.SetCameraFocus(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "getprimaryfocus":
                result = new CogValue(_context.GetPrimaryFocus(Arg(0).AsInt));
                return true;

            case "setmusicvol":
                _context.SetMusicVol(Arg(0).AsFloat);
                result = CogValue.Null;
                return true;

            case "playsong":
                _context.PlaySong(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "enableirmode":
                _context.EnableIRMode(Arg(0).AsFloat, Arg(1).AsFloat);
                result = CogValue.Null;
                return true;

            case "disableirmode":
                _context.DisableIRMode();
                result = CogValue.Null;
                return true;

            case "newcoloreffect":
                result = new CogValue(_context.NewColorEffect(
                    Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt,
                    Arg(3).AsFloat, Arg(4).AsFloat, Arg(5).AsFloat,
                    Arg(6).AsInt, Arg(7).AsInt, Arg(8).AsInt,
                    Arg(9).AsFloat));
                return true;

            case "freecoloreffect":
                _context.FreeColorEffect(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "setmapmodeflags":
                _context.SetMapModeFlags(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "clearmapmodeflags":
                _context.ClearMapModeFlags(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "getmapmodeflags":
                result = new CogValue(_context.GetMapModeFlags());
                return true;

            case "setpovshake":
                _context.SetPovShake(Arg(0).AsVector3, Arg(1).AsVector3, Arg(2).AsFloat, Arg(3).AsFloat);
                result = CogValue.Null;
                return true;

            // Level, JK saber/HUD & UI verbs
            case "jkendlevel":
                _context.JkEndLevel(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "autosavegame":
                _context.AutosaveGame();
                result = CogValue.Null;
                return true;

            case "jkprintunistring":
                _context.JkPrintUniString(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "jksetflags":
                _context.JkSetFlags(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "jkgetflags":
                result = new CogValue(_context.JkGetFlags(Arg(0).AsInt));
                return true;

            case "jkclearflags":
                _context.JkClearFlags(Arg(0).AsInt, Arg(1).AsInt);
                result = CogValue.Null;
                return true;

            case "jksetweaponmesh":
                _context.JkSetWeaponMesh(Arg(0).AsInt, Arg(1).AsString);
                result = CogValue.Null;
                return true;

            case "jkgetweaponmesh":
                result = new CogValue(_context.JkGetWeaponMesh(Arg(0).AsInt));
                return true;

            case "jkenablesaber":
                _context.JkEnableSaber(Arg(0).AsInt, Arg(1).AsFloat, Arg(2).AsFloat, Arg(3).AsFloat);
                result = CogValue.Null;
                return true;

            case "jkdisablesaber":
                _context.JkDisableSaber(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "jksetsaberinfo":
                _context.JkSetSaberInfo(
                    Arg(0).AsInt, Arg(1).AsString, Arg(2).AsString,
                    Arg(3).AsFloat, Arg(4).AsFloat, Arg(5).AsFloat,
                    Arg(6).AsString, Arg(7).AsString, Arg(8).AsString);
                result = CogValue.Null;
                return true;

            case "jksetpersuasioninfo":
                _context.JkSetPersuasionInfo(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "jksetpovmodel":
                _context.JkSetPovModel(Arg(0).AsInt, Arg(1).AsString);
                result = CogValue.Null;
                return true;

            case "jkplaypovkey":
                result = new CogValue(_context.JkPlayPovKey(Arg(0).AsInt, Arg(1).AsString, Arg(2).AsInt, Arg(3).AsInt));
                return true;

            case "jkstoppovkey":
                _context.JkStopPovKey(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "jksetwaggle":
                _context.JkSetWaggle(Arg(0).AsInt, Arg(1).AsVector3, Arg(2).AsFloat);
                result = CogValue.Null;
                return true;

            case "jkgetsabercam":
                result = new CogValue(_context.JkGetSaberCam());
                return true;

            case "jksettarget":
                _context.JkSetTarget(Arg(0).AsInt);
                result = CogValue.Null;
                return true;

            case "jkendtarget":
                _context.JkEndTarget();
                result = CogValue.Null;
                return true;

            case "jksettargetcolors":
                _context.JkSetTargetColors(Arg(0).AsInt, Arg(1).AsInt, Arg(2).AsInt);
                result = CogValue.Null;
                return true;

            case "jkbegincutscene":
                _context.JkBeginCutscene();
                result = CogValue.Null;
                return true;

            case "jkendcutscene":
                _context.JkEndCutscene();
                result = CogValue.Null;
                return true;

            case "getmajormode":
                result = new CogValue(_context.GetMajorMode());
                return true;

            case "getdifficulty":
                result = new CogValue(_context.GetDifficulty());
                return true;

            case "ismulti":
                result = new CogValue(_context.IsMulti());
                return true;

            case "randvec":
                result = new CogValue(new Vector3(
                    _context.Rand() * 2f - 1f,
                    _context.Rand() * 2f - 1f,
                    _context.Rand() * 2f - 1f));
                return true;

            // Math / Vector / Bit verbs
            case "vectorset":
                result = new CogValue(new Vector3(Arg(0).AsFloat, Arg(1).AsFloat, Arg(2).AsFloat));
                return true;

            case "vectoradd":
                result = new CogValue(Arg(0).AsVector3 + Arg(1).AsVector3);
                return true;

            case "vectorsub":
                result = new CogValue(Arg(0).AsVector3 - Arg(1).AsVector3);
                return true;

            case "vectorscale":
                result = new CogValue(Arg(0).AsVector3 * Arg(1).AsFloat);
                return true;

            case "vectordist":
                result = new CogValue(Vector3.Distance(Arg(0).AsVector3, Arg(1).AsVector3));
                return true;

            case "vectordot":
                result = new CogValue(Vector3.Dot(Arg(0).AsVector3, Arg(1).AsVector3));
                return true;

            case "vectorcross":
                result = new CogValue(Vector3.Cross(Arg(0).AsVector3, Arg(1).AsVector3));
                return true;

            case "vectornorm":
            {
                var vn = Arg(0).AsVector3;
                result = new CogValue(vn != Vector3.Zero ? Vector3.Normalize(vn) : Vector3.Zero);
                return true;
            }

            case "vectorlen":
                result = new CogValue(Arg(0).AsVector3.Length());
                return true;

            case "vectorx":
                result = new CogValue(Arg(0).AsVector3.X);
                return true;

            case "vectory":
                result = new CogValue(Arg(0).AsVector3.Y);
                return true;

            case "vectorz":
                result = new CogValue(Arg(0).AsVector3.Z);
                return true;

            case "bittest":
                result = new CogValue((Arg(0).AsInt & Arg(1).AsInt) != 0 ? 1 : 0);
                return true;

            case "bitset":
                result = new CogValue(Arg(0).AsInt | Arg(1).AsInt);
                return true;

            case "bitclear":
                result = new CogValue(Arg(0).AsInt & ~Arg(1).AsInt);
                return true;

            default:
                result = CogValue.Null;
                return false;
        }
    }

    private static bool TryResolveMessageLabel(CogInstance instance, string message, out int labelPc)
    {
        if (instance.Program.Labels.TryGetValue(message, out labelPc))
            return true;

        if (message.Equals("activate", StringComparison.OrdinalIgnoreCase))
            return instance.Program.Labels.TryGetValue("activated", out labelPc);

        if (message.Equals("activated", StringComparison.OrdinalIgnoreCase))
            return instance.Program.Labels.TryGetValue("activate", out labelPc);

        return false;
    }

    private static string ResolveMessageName(CallVerbExpr call, IReadOnlyList<CogValue> args, int argIdx)
    {
        if (argIdx < args.Count && args[argIdx].Type != CogValueType.Null)
            return args[argIdx].AsString;

        if (argIdx < call.Arguments.Count && call.Arguments[argIdx] is Identifier id)
            return id.Name;

        return string.Empty;
    }

    private int ResolveSenderId(CogExecutionContext context)
    {
        if (context.SenderId >= 0)
            return context.SenderId;

        int contextSenderId = _context.GetSenderId();
        if (contextSenderId >= 0)
            return contextSenderId;

        if (context.SenderRef >= 0)
        {
            var symbols = context.Instance.Script.Symbols;
            var values = context.Instance.SymbolValues;
            for (int i = 0; i < symbols.Count && i < values.Length; i++)
            {
                var sym = symbols[i];
                if (sym.LinkId >= 0 &&
                    sym.Type is CogSymbolType.Thing or CogSymbolType.Sector or CogSymbolType.Surface or CogSymbolType.Cog &&
                    values[i].Type == CogValueType.Int &&
                    values[i].AsInt == context.SenderRef)
                {
                    return sym.LinkId;
                }
            }
        }

        return context.SenderRef;
    }

    /// <summary>
    /// Returns true if the given COG verb name is handled by <see cref="CogVm"/>.
    /// </summary>
    public static bool IsVerbSupported(string verbName) =>
        !string.IsNullOrEmpty(verbName) && SupportedVerbs.Contains(verbName);

    private static readonly HashSet<string> SupportedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "getthingsector", "getthingpos", "movetoframe", "isthingmoving", "ismoving", "waitforstop", "getcurframe",
        "getlocalplayerthing", "jkgetlocalplayer", "setsectoradjoins", "sectoradjoins", "setsectorlight",
        "sectorlight", "sectorthrust", "playsoundthing", "sendmessage", "sendmessageex", "getselfcog", "setmastercog",
        "getmastercog", "print", "printint", "printflex", "rand", "getsenderref", "getsenderid", "getsourceref", "getleveltime", "getparam",
        "settimer", "setpulse", "settimerex", "killtimerex", "sleep", "returnex", "stop",
        "creatething", "createthingnr", "createthingatpos", "createthingatposnr", "destroything", "setlifeleft", "getlifeleft",
        "damagething", "healthing", "getthinghealth", "gethealth", "setthinghealth", "getthingmaxhealth", "getthingtemplate",
        "setthingvel", "addthingvel", "getthingvel", "setthingrotvel", "getthingrotvel", "applyforce",
        "stopthing", "thinglight", "setthinglight", "getthinglight", "playkey", "stopkey", "playmode",
        "getthingflags", "setthingflags", "clearthingflags", "getactorflags", "setactorflags", "clearactorflags",
        "getphysicsflags", "setphysicsflags", "clearphysicsflags", "getitemflags", "getattachflags",
        "attachthingtothingex", "detachthing", "getthingclasscog", "getthinggeomode", "setthingcurgeomode",
        "getthingparent", "getthingsignature", "getthingthrust", "getthingtype", "getthinguserdata",
        "setthinguserdata", "setthinglook", "getthinglvec", "capturething", "setthingpulse", "setthingcollidesize", "setcollidetype",
        "setactorextraspeed", "setactorweapon", "setarmedmode",
        "setparticlegrowthspeed", "parsearg", "getcursector",
        "changeinv", "getinv", "setinv", "getinvmax", "setgoalflags", "cleargoalflags", "getgoalflags",
        "takeitem", "adddynamictint", "adddynamicadd", "getautoreload", "getautopickup", "getautoswitch",
        "autoselectweapon", "selectweapon", "getcurweapon", "getcurweaponmode", "getweaponpriority", "setweaponflags", "changefirerate",
        "createbackpack", "pickupbackpack",
        "setcurinvweapon", "getcurinvweapon", "setcurweapon", "activatebin", "deactivatebin",
        "isinvactivated", "setinvactivated", "setinvavailable", "isinvavailable", "setbinwait",
        "activateweapon", "deactivateweapon", "fireprojectile", "setfirewait", "setmountwait",
        "amputatejoint", "aisetclass", "aiflee", "aisetlookframe", "aisetmoveframe", "aisetlookpos", "aisetmovepos", "aisetmovespeed", "aijump",
        "aigetmode", "aisetmode", "aiclearmode",
        "getactorweapon", "skilltarget", "firstthinginview", "nextthinginview", "haslos", "thingviewdot",
        "setsurfacecel", "setwallcel", "getsurfacecel", "getwallcel", "slidesurface", "slidewall", "slideceilingsky", "surfaceanim",
        "stopsurfaceanim", "materialanim", "surfacecenter", "getsurfacecenter", "playsoundlocal", "playsoundpos", "stopsound",
        "changesoundpitch", "changesoundvol", "getsoundlen", "getkeylen", "setmusicvol", "playsong", "cyclecamera", "getcurrentcamera",
        "setcurrentcamera", "setcamerafocus", "getprimaryfocus", "enableirmode", "disableirmode", "newcoloreffect", "freecoloreffect",
        "setmapmodeflags", "clearmapmodeflags", "getmapmodeflags", "setpovshake",
        "jkendlevel", "autosavegame", "jkprintunistring", "jksetflags", "jkgetflags", "jkclearflags",
        "jksetweaponmesh", "jkgetweaponmesh", "jkenablesaber", "jkdisablesaber", "jksetsaberinfo",
        "jksetpersuasioninfo", "jksetpovmodel", "jkplaypovkey", "jkstoppovkey", "jksetwaggle",
        "jkgetsabercam", "jksettarget", "jkendtarget", "jksettargetcolors", "jkbegincutscene",
        "jkendcutscene", "getmajormode", "getdifficulty", "ismulti", "randvec",
        "vectorset", "vectoradd", "vectorsub", "vectorscale", "vectordist", "vectordot",
        "vectorcross", "vectornorm", "vectorlen", "vectorx", "vectory", "vectorz",
        "bittest", "bitset", "bitclear"
    };

    private int ResolveSourceRef(CogExecutionContext context)
    {
        if (context.SourceRef >= 0)
            return context.SourceRef;

        int contextSourceRef = _context.GetSourceRef();
        if (contextSourceRef >= 0)
            return contextSourceRef;

        return context.SenderRef;
    }
}
