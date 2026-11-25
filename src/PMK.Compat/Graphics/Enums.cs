namespace PMK.Compat.Graphics
{
    // XNA 3.1 enums whose numeric values or meaning differ from XNA 4.0 / FNA.
    // The relinked game passes these values as raw integer constants, so the
    // values below must match XNA 3.1 exactly.

    public enum SurfaceFormat
    {
        Unknown = -1,
        Color = 1,
        Bgr32 = 2,
        Bgra1010102 = 3,
        Rgba32 = 4,
        Rgb32 = 5,
        Rgba1010102 = 6,
        Rg32 = 7,
        Rgba64 = 8,
        Bgr565 = 9,
        Bgra5551 = 10,
        Bgr555 = 11,
        Bgra4444 = 12,
        Bgr444 = 13,
        Bgra2338 = 14,
        Alpha8 = 15,
        Bgr233 = 16,
        Bgr24 = 17,
        NormalizedByte2 = 18,
        NormalizedByte4 = 19,
        NormalizedShort2 = 20,
        NormalizedShort4 = 21,
        Single = 22,
        Vector2 = 23,
        Vector4 = 24,
        HalfSingle = 25,
        HalfVector2 = 26,
        HalfVector4 = 27,
        Dxt1 = 28,
        Dxt2 = 29,
        Dxt3 = 30,
        Dxt4 = 31,
        Dxt5 = 32,
    }

    public enum ShaderProfile
    {
        PS_1_1 = 0, PS_1_2 = 1, PS_1_3 = 2, PS_1_4 = 3, PS_2_0 = 4, PS_2_A = 5, PS_2_B = 6, PS_2_SW = 7, PS_3_0 = 8, XPS_3_0 = 9,
        VS_1_1 = 10, VS_2_0 = 11, VS_2_A = 12, VS_2_SW = 13, VS_3_0 = 14, XVS_3_0 = 15, Unknown = 16,
    }

    public enum SpriteBlendMode
    {
        None = 0,
        AlphaBlend = 1,
        Additive = 2,
    }

    public enum SaveStateMode
    {
        None = 0,
        SaveState = 1,
    }

    public enum TextureFilter
    {
        None = 0,
        Point = 1,
        Linear = 2,
        Anisotropic = 3,
        PyramidalQuad = 6,
        GaussianQuad = 7,
    }

    public enum CullMode
    {
        None = 1,
        CullClockwiseFace = 2,
        CullCounterClockwiseFace = 3,
    }
}
