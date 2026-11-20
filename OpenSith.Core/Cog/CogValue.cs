using System;
using System.Globalization;
using System.Numerics;

namespace OpenSith.Core.Cog;

/// <summary>
/// Value type tag for runtime COG values.
/// </summary>
public enum CogValueType : byte
{
    Null = 0,
    Int = 1,
    Float = 2,
    Vector = 3,
    String = 4
}

/// <summary>
/// Lightweight value type representing a COG runtime value (Int, Float, Vector3, String).
/// Matches LucasArts COG interop semantics where numeric and reference types interchange cleanly.
/// </summary>
public readonly struct CogValue : IEquatable<CogValue>, IComparable<CogValue>
{
    public CogValueType Type { get; }
    private readonly int _intVal;
    private readonly float _floatVal;
    private readonly Vector3 _vecVal;
    private readonly string? _strVal;

    public static readonly CogValue Null = new(CogValueType.Null, 0, 0f, Vector3.Zero, null);

    private CogValue(CogValueType type, int intVal, float floatVal, Vector3 vecVal, string? strVal)
    {
        Type = type;
        _intVal = intVal;
        _floatVal = floatVal;
        _vecVal = vecVal;
        _strVal = strVal;
    }

    public CogValue(int value) : this(CogValueType.Int, value, value, Vector3.Zero, null) { }

    public CogValue(float value) : this(CogValueType.Float, (int)value, value, Vector3.Zero, null) { }

    public CogValue(bool value) : this(CogValueType.Int, value ? 1 : 0, value ? 1f : 0f, Vector3.Zero, null) { }

    public CogValue(Vector3 value) : this(CogValueType.Vector, 0, 0f, value, null) { }

    public CogValue(string? value) : this(CogValueType.String, 0, 0f, Vector3.Zero, value ?? string.Empty) { }

    public int AsInt
    {
        get
        {
            return Type switch
            {
                CogValueType.Int => _intVal,
                CogValueType.Float => (int)_floatVal,
                CogValueType.String => ParseInt(_strVal),
                _ => 0
            };
        }
    }

    public float AsFloat
    {
        get
        {
            return Type switch
            {
                CogValueType.Float => _floatVal,
                CogValueType.Int => _intVal,
                CogValueType.String => ParseFloat(_strVal),
                _ => 0f
            };
        }
    }

    public bool AsBool
    {
        get
        {
            return Type switch
            {
                CogValueType.Int => _intVal != 0,
                CogValueType.Float => _floatVal != 0f,
                CogValueType.Vector => _vecVal != Vector3.Zero,
                CogValueType.String => !string.IsNullOrEmpty(_strVal) && _strVal != "0" && _strVal != "0.0",
                _ => false
            };
        }
    }

    public Vector3 AsVector3 => Type == CogValueType.Vector ? _vecVal : Vector3.Zero;

    public string AsString
    {
        get
        {
            return Type switch
            {
                CogValueType.String => _strVal ?? string.Empty,
                CogValueType.Int => _intVal.ToString(CultureInfo.InvariantCulture),
                CogValueType.Float => _floatVal.ToString("0.######", CultureInfo.InvariantCulture),
                CogValueType.Vector => $"({_vecVal.X:F6} {_vecVal.Y:F6} {_vecVal.Z:F6})",
                _ => string.Empty
            };
        }
    }

    private static int ParseInt(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return 0;
        if (int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            return i;
        if (float.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            return (int)f;
        return 0;
    }

    private static float ParseFloat(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return 0f;
        if (float.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            return f;
        return 0f;
    }

    public static implicit operator CogValue(int val) => new(val);
    public static implicit operator CogValue(float val) => new(val);
    public static implicit operator CogValue(bool val) => new(val);
    public static implicit operator CogValue(Vector3 val) => new(val);
    public static implicit operator CogValue(string? val) => new(val);

    public static explicit operator int(CogValue val) => val.AsInt;
    public static explicit operator float(CogValue val) => val.AsFloat;
    public static explicit operator bool(CogValue val) => val.AsBool;
    public static explicit operator Vector3(CogValue val) => val.AsVector3;
    public static explicit operator string(CogValue val) => val.AsString;

    public static CogValue operator +(CogValue a, CogValue b)
    {
        if (a.Type == CogValueType.String || b.Type == CogValueType.String)
            return new CogValue(a.AsString + b.AsString);
        if (a.Type == CogValueType.Vector || b.Type == CogValueType.Vector)
            return new CogValue(a.AsVector3 + b.AsVector3);
        if (a.Type == CogValueType.Float || b.Type == CogValueType.Float)
            return new CogValue(a.AsFloat + b.AsFloat);
        return new CogValue(a.AsInt + b.AsInt);
    }

    public static CogValue operator -(CogValue a, CogValue b)
    {
        if (a.Type == CogValueType.Vector || b.Type == CogValueType.Vector)
            return new CogValue(a.AsVector3 - b.AsVector3);
        if (a.Type == CogValueType.Float || b.Type == CogValueType.Float)
            return new CogValue(a.AsFloat - b.AsFloat);
        return new CogValue(a.AsInt - b.AsInt);
    }

    public static CogValue operator *(CogValue a, CogValue b)
    {
        if (a.Type == CogValueType.Vector)
            return new CogValue(a.AsVector3 * b.AsFloat);
        if (b.Type == CogValueType.Vector)
            return new CogValue(b.AsVector3 * a.AsFloat);
        if (a.Type == CogValueType.Float || b.Type == CogValueType.Float)
            return new CogValue(a.AsFloat * b.AsFloat);
        return new CogValue(a.AsInt * b.AsInt);
    }

    public static CogValue operator /(CogValue a, CogValue b)
    {
        if (a.Type == CogValueType.Vector)
        {
            float div = b.AsFloat;
            return div != 0f ? new CogValue(a.AsVector3 / div) : a;
        }
        if (a.Type == CogValueType.Float || b.Type == CogValueType.Float)
        {
            float div = b.AsFloat;
            return div != 0f ? new CogValue(a.AsFloat / div) : new CogValue(0f);
        }
        int bInt = b.AsInt;
        return bInt != 0 ? new CogValue(a.AsInt / bInt) : new CogValue(0);
    }

    public static CogValue operator %(CogValue a, CogValue b)
    {
        int bInt = b.AsInt;
        return bInt != 0 ? new CogValue(a.AsInt % bInt) : new CogValue(0);
    }

    public static CogValue operator &(CogValue a, CogValue b) => new(a.AsInt & b.AsInt);
    public static CogValue operator |(CogValue a, CogValue b) => new(a.AsInt | b.AsInt);
    public static CogValue operator ^(CogValue a, CogValue b) => new(a.AsInt ^ b.AsInt);

    public static CogValue operator -(CogValue a)
    {
        return a.Type switch
        {
            CogValueType.Vector => new CogValue(-a.AsVector3),
            CogValueType.Float => new CogValue(-a.AsFloat),
            _ => new CogValue(-a.AsInt)
        };
    }

    public static CogValue operator +(CogValue a) => a;

    public static CogValue operator ~(CogValue a) => new(~a.AsInt);

    public static CogValue operator !(CogValue a) => new(a.AsBool ? 0 : 1);

    public static bool operator ==(CogValue a, CogValue b) => a.Equals(b);
    public static bool operator !=(CogValue a, CogValue b) => !a.Equals(b);

    public static bool operator <(CogValue a, CogValue b) => a.CompareTo(b) < 0;
    public static bool operator <=(CogValue a, CogValue b) => a.CompareTo(b) <= 0;
    public static bool operator >(CogValue a, CogValue b) => a.CompareTo(b) > 0;
    public static bool operator >=(CogValue a, CogValue b) => a.CompareTo(b) >= 0;

    public bool Equals(CogValue other)
    {
        if (Type == CogValueType.Null && other.Type == CogValueType.Null)
            return true;

        if (Type == CogValueType.Vector || other.Type == CogValueType.Vector)
            return AsVector3 == other.AsVector3;

        // If one is string and one is numeric, try parsing string as float
        if (Type == CogValueType.String ^ other.Type == CogValueType.String)
        {
            string str = Type == CogValueType.String ? AsString : other.AsString;
            float num = Type == CogValueType.String ? other.AsFloat : AsFloat;
            if (float.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return Math.Abs(parsed - num) < 0.0001f;
            return false;
        }

        if (Type == CogValueType.String && other.Type == CogValueType.String)
            return string.Equals(AsString, other.AsString, StringComparison.OrdinalIgnoreCase);

        if (Type == CogValueType.Float || other.Type == CogValueType.Float)
            return Math.Abs(AsFloat - other.AsFloat) < 0.0001f;

        return AsInt == other.AsInt;
    }

    public override bool Equals(object? obj) => obj is CogValue other && Equals(other);

    public override int GetHashCode()
    {
        if (Type == CogValueType.String)
            return StringComparer.OrdinalIgnoreCase.GetHashCode(AsString);
        if (Type == CogValueType.Vector)
            return _vecVal.GetHashCode();
        if (Type == CogValueType.Float)
            return _floatVal.GetHashCode();
        return _intVal.GetHashCode();
    }

    public int CompareTo(CogValue other)
    {
        if (Type == CogValueType.String && other.Type == CogValueType.String)
            return string.Compare(AsString, other.AsString, StringComparison.OrdinalIgnoreCase);
        return AsFloat.CompareTo(other.AsFloat);
    }

    public override string ToString() => AsString;
}
