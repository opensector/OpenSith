using System.Collections.Generic;
using System.Numerics;

namespace OpenSith.Core.Cog;

public enum CogBinaryOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    LogicalAnd,
    LogicalOr,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}

public enum CogUnaryOp
{
    Negate,
    Not,
    BitwiseNot,
    Plus
}

public abstract record CogStmt;

public sealed record Block(List<CogStmt> Statements) : CogStmt;
public sealed record @If(CogExpr Condition, CogStmt ThenBranch, CogStmt? ElseBranch = null) : CogStmt;
public sealed record @For(CogStmt? Init, CogExpr? Condition, CogExpr? Step, CogStmt Body) : CogStmt;
public sealed record @While(CogExpr Condition, CogStmt Body) : CogStmt;
public sealed record DoWhile(CogStmt Body, CogExpr Condition) : CogStmt;
public sealed record @Return(CogExpr? Value = null) : CogStmt;
public sealed record @Break() : CogStmt;
public sealed record @Continue() : CogStmt;
public sealed record CallLabel(string Label) : CogStmt;
public sealed record @Goto(string Label) : CogStmt;
public sealed record Label(string Name) : CogStmt;
public sealed record ExprStmt(CogExpr Expression) : CogStmt;

public abstract record CogExpr;

public sealed record IntLiteral(int Value) : CogExpr;
public sealed record FloatLiteral(float Value) : CogExpr;
public sealed record StringLiteral(string Value) : CogExpr;
public sealed record VectorLiteral(Vector3 Value) : CogExpr;
public sealed record Identifier(string Name) : CogExpr;
public sealed record IndexExpr(CogExpr Target, CogExpr Index) : CogExpr;
public sealed record BinaryExpr(CogBinaryOp Op, CogExpr Left, CogExpr Right) : CogExpr;
public sealed record UnaryExpr(CogUnaryOp Op, CogExpr Operand) : CogExpr;
public sealed record AssignExpr(CogExpr Target, CogExpr Value) : CogExpr;
public sealed record CallVerbExpr(string VerbName, List<CogExpr> Arguments) : CogExpr;
