using System.Runtime.InteropServices;

internal sealed unsafe class DashboardTexture : IDisposable
{
    private const uint BgraSupport = 0x20, Bgra8Unorm = 87, ShaderResourceAndRenderTarget = 0x28, Shared = 0x2;
    private const uint D3d11SdkVersion = 7;
    private const int HardwareDriver = 1, AdapterDriver = 0;
    private const int EnumAdapters1 = 12, CreateTexture2D = 5, CreateQuery = 24;
    private const int End = 28, GetData = 29, UpdateSubresource = 48, Flush = 111;
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1

    private nint device, context, uploadComplete;
    private readonly nint[] textures = new nint[2];
    private int nextTexture;

    public static DashboardTexture? TryCreate(int adapterIndex)
    {
        nint factory = 0, adapter = 0;
        var target = new DashboardTexture();
        bool created = false;
        try
        {
            if (adapterIndex >= 0 &&
                (CreateDXGIFactory1(in FactoryId, out factory) < 0 ||
                ((delegate* unmanaged<nint, uint, nint*, int>)Slot(factory, EnumAdapters1))
                    (factory, (uint)adapterIndex, &adapter) < 0))
                return null;

            if (D3D11CreateDevice(adapter, adapter == 0 ? HardwareDriver : AdapterDriver, 0, BgraSupport,
                0, 0, D3d11SdkVersion, out target.device, 0, out target.context) < 0)
                return null;

            var description = new TextureDescription
            {
                Width = DashboardPanel.Width, Height = DashboardPanel.Height,
                MipLevels = 1, ArraySize = 1, Format = Bgra8Unorm, SampleCount = 1,
                BindFlags = ShaderResourceAndRenderTarget, MiscFlags = Shared
            };
            for (int i = 0; i < target.textures.Length; i++)
            {
                nint texture = 0;
                if (((delegate* unmanaged<nint, TextureDescription*, nint, nint*, int>)Slot(target.device, CreateTexture2D))
                    (target.device, &description, 0, &texture) < 0)
                    return null;
                target.textures[i] = texture;
            }

            var query = new QueryDescription(); // D3D11_QUERY_EVENT
            nint completion = 0;
            if (((delegate* unmanaged<nint, QueryDescription*, nint*, int>)Slot(target.device, CreateQuery))
                (target.device, &query, &completion) < 0)
                return null;
            target.uploadComplete = completion;
            created = true;
            return target;
        }
        finally
        {
            if (!created) target.Dispose();
            Release(ref adapter);
            Release(ref factory);
        }
    }

    public nint Upload(byte[] bgra)
    {
        const uint rowPitch = DashboardPanel.Width * 4;
        if (bgra.Length != rowPitch * DashboardPanel.Height)
            throw new ArgumentException("Dashboard pixels must fill the texture.", nameof(bgra));

        // Follow Tracker Smoothing: write the back texture and wait before handing it to SteamVR.
        nint texture = textures[nextTexture];
        fixed (byte* pixels = bgra)
            ((delegate* unmanaged<nint, nint, uint, nint, byte*, uint, uint, void>)Slot(context, UpdateSubresource))
                (context, texture, 0, 0, pixels, rowPitch, 0);
        ((delegate* unmanaged<nint, nint, void>)Slot(context, End))(context, uploadComplete);
        ((delegate* unmanaged<nint, void>)Slot(context, Flush))(context);

        long deadline = Environment.TickCount64 + 200;
        int result;
        while ((result = ((delegate* unmanaged<nint, nint, nint, uint, uint, int>)Slot(context, GetData))
            (context, uploadComplete, 0, 0, 0)) == 1)
        {
            if (Environment.TickCount64 >= deadline) return 0;
            Thread.Sleep(0);
        }
        if (result != 0) return 0;

        nextTexture ^= 1;
        return texture;
    }

    public void Dispose()
    {
        Release(ref uploadComplete);
        for (int i = 0; i < textures.Length; i++) Release(ref textures[i]);
        Release(ref context);
        Release(ref device);
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Release(ref nint instance)
    {
        if (instance == 0) return;
        Marshal.Release(instance);
        instance = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality;
        public uint Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryDescription
    {
        public uint Query, MiscFlags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid interfaceId, out nint factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, nint featureLevel, out nint context);
}
