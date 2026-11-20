using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace OpenSith.Client;

public sealed class ShaderProgram : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; }

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;

        uint vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        uint fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vertexShader);
        _gl.AttachShader(Handle, fragmentShader);
        _gl.LinkProgram(Handle);

        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string infoLog = _gl.GetProgramInfoLog(Handle);
            throw new InvalidOperationException($"Error linking shader program: {infoLog}");
        }

        _gl.DetachShader(Handle, vertexShader);
        _gl.DetachShader(Handle, fragmentShader);
        _gl.DeleteShader(vertexShader);
        _gl.DeleteShader(fragmentShader);
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string infoLog = _gl.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"Error compiling {type}: {infoLog}");
        }

        return shader;
    }

    public void Use() => _gl.UseProgram(Handle);

    public void SetUniform(string name, int value)
    {
        int location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
            _gl.Uniform1(location, value);
    }

    public void SetUniform(string name, float value)
    {
        int location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
            _gl.Uniform1(location, value);
    }

    public void SetUniform(string name, Vector3 value)
    {
        int location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
            _gl.Uniform3(location, value.X, value.Y, value.Z);
    }

    public unsafe void SetUniform(string name, Matrix4x4 matrix)
    {
        int location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.UniformMatrix4(location, 1, false, (float*)&matrix);
        }
    }

    public void Dispose()
    {
        _gl.DeleteProgram(Handle);
    }
}
