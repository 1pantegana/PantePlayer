using LettoreVideo.Engine.Interop;

namespace LettoreVideo.Engine.Gpu;

/// <summary>A pooled video texture plus the processor input view cached on it.</summary>
internal sealed class PooledTexture : IDisposable
{
    private readonly TexturePool _owner;

    internal PooledTexture(TexturePool owner, D3D11Texture2D texture, TexturePool.Key key)
    {
        _owner = owner;
        Texture = texture;
        PoolKey = key;
    }

    public D3D11Texture2D Texture { get; }
    internal TexturePool.Key PoolKey { get; }

    /// <summary>Input view for the video processor generation it was created for.</summary>
    internal ComObject? InputView;
    internal int InputViewGeneration = -1;

    public void Return() => _owner.Return(this);

    public void Dispose()
    {
        InputView?.Dispose();
        InputView = null;
        Texture.Dispose();
    }
}

/// <summary>
/// Recycles the textures that decoded (and generated) frames live in. Textures of
/// the current size and format are reused; a new format retires the old ones as
/// they come back.
/// </summary>
internal sealed class TexturePool : IDisposable
{
    internal readonly record struct Key(int Width, int height, DxgiFormat format);

    private readonly D3D11Device _device;
    private readonly object _sync = new();
    private readonly Stack<PooledTexture> _free = new();
    private Key _key;
    private bool _disposed;

    public TexturePool(D3D11Device device) => _device = device;

    public PooledTexture Rent(int width, int height, DxgiFormat format)
    {
        var key = new Key(width, height, format);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (key != _key)
            {
                while (_free.Count > 0) _free.Pop().Dispose();
                _key = key;
            }

            if (_free.Count > 0) return _free.Pop();
        }

        var desc = Texture2DDesc.Create(format, width, height, D3D11.BindShaderResource | D3D11.BindRenderTarget);
        return new PooledTexture(this, _device.CreateTexture2D(desc), key);
    }

    internal void Return(PooledTexture texture)
    {
        lock (_sync)
        {
            if (!_disposed && texture.PoolKey == _key && _free.Count < 64)
            {
                _free.Push(texture);
                return;
            }
        }

        texture.Dispose();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            while (_free.Count > 0) _free.Pop().Dispose();
        }
    }
}


