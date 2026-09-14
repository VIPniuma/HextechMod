using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 纯代码构建 UGUI 的小工具集，避免依赖游戏内的 UI 预制体。
/// 所有贴图（圆角、光晕、圆环、渐变）都是运行时按需生成并缓存的一次性资源，
/// 不占用任何美术资源，也不怕场景切换。
/// </summary>
internal static class UiFactory
{
    // ── 配色：对齐 PEAK 原生界面的暖色纸质感，避免自成一派的冷蓝科幻风 ──
    /// <summary>面板底色（深棕，略透）。</summary>
    public static readonly Color PanelBackground = new(0.086f, 0.078f, 0.067f, 0.965f);

    /// <summary>卡片 / 次级块底色（浅一档的棕）。</summary>
    public static readonly Color PanelBackgroundLight = new(0.157f, 0.137f, 0.114f, 1f);

    /// <summary>按钮 / 高亮块底色。</summary>
    public static readonly Color PanelHighlight = new(0.212f, 0.192f, 0.161f, 1f);

    /// <summary>标题栏底色。</summary>
    public static readonly Color HeaderBackground = new(0.129f, 0.113f, 0.094f, 1f);

    /// <summary>遮罩用的暗色（带一点暖调，不是纯黑）。</summary>
    public static readonly Color Backdrop = new(0.035f, 0.030f, 0.024f, 0.80f);

    public static readonly Color TextPrimary = new(0.949f, 0.925f, 0.855f, 1f);
    public static readonly Color TextMuted = new(0.678f, 0.643f, 0.580f, 1f);
    public static readonly Color Accent = new(0.902f, 0.639f, 0.196f, 1f);
    public static readonly Color Success = new(0.286f, 0.545f, 0.271f, 1f);
    public static readonly Color Warning = new(1f, 0.82f, 0.42f, 1f);
    public static readonly Color Danger = new(0.804f, 0.325f, 0.294f, 1f);

    private static readonly Dictionary<int, Sprite> RoundedCache = new();
    private static Sprite? _glowSprite;
    private static Sprite? _circleSprite;
    private static Sprite? _ringSprite;
    private static Sprite? _gradientSprite;

    /// <summary>已创建的所有文字，用来在字体就绪后统一补上中文字形。</summary>
    private static readonly List<TextMeshProUGUI> Labels = new();

    private static readonly HashSet<TMP_FontAsset> FallbackRegistered = new();

    public static Color QualityColor(HextechQuality quality)
    {
        return quality switch
        {
            HextechQuality.Legendary => new Color(0.82f, 0.42f, 1f, 1f),
            HextechQuality.Gold => new Color(1f, 0.79f, 0.28f, 1f),
            HextechQuality.Silver => new Color(0.80f, 0.85f, 0.92f, 1f),
            _ => new Color(0.83f, 0.55f, 0.28f, 1f),
        };
    }

    public static string QualityName(HextechQuality quality)
    {
        return quality switch
        {
            HextechQuality.Legendary => "传说",
            HextechQuality.Gold => "黄金",
            HextechQuality.Silver => "白银",
            _ => "青铜",
        };
    }

    /// <summary>品质徽章上用的符号，避免为每个品质准备贴图。</summary>
    public static string QualityGlyph(HextechQuality quality)
    {
        return quality switch
        {
            HextechQuality.Legendary => "※",
            HextechQuality.Gold => "★",
            HextechQuality.Silver => "◆",
            _ => "●",
        };
    }

    // ── 字体 ─────────────────────────────────────────────────────
    /// <summary>
    /// 取游戏当前使用的字体。注意游戏的主字体只有拉丁字形表，
    /// 直接用它会看到「中文全是方框」；必须把游戏自带的中文字体
    /// （<see cref="FontFallbackSwapper.simplifiedChineseFont"/>）挂进 fallback 表才会出字。
    /// </summary>
    public static TMP_FontAsset? ResolveFont()
    {
        var swapper = FontFallbackSwapper.instance;

        if (swapper != null)
        {
            var main = swapper.mainBaseFont;
            var chinese = swapper.simplifiedChineseFont;

            if (main != null)
            {
                if (chinese != null)
                {
                    RegisterFallback(main, chinese);
                }

                return main;
            }

            if (chinese != null)
            {
                return chinese;
            }
        }

        return TMP_Settings.defaultFontAsset;
    }

    /// <summary>
    /// <see cref="FontFallbackSwapper"/> 只会在游戏里的场景出现，
    /// 而 UI 是在插件加载时就建好的，所以每帧重新对一次字体，
    /// 等它出现后把中文 fallback 补上，方框就会变成汉字。
    /// </summary>
    public static void RefreshFonts()
    {
        var font = ResolveFont();

        if (font == null)
        {
            return;
        }

        for (var i = Labels.Count - 1; i >= 0; i--)
        {
            var label = Labels[i];

            if (label == null)
            {
                Labels.RemoveAt(i);
                continue;
            }

            if (label.font != font)
            {
                label.font = font;
            }
        }
    }

    private static void RegisterFallback(TMP_FontAsset main, TMP_FontAsset chinese)
    {
        if (!FallbackRegistered.Add(main))
        {
            return;
        }

        try
        {
            var table = main.fallbackFontAssetTable;

            if (table == null)
            {
                table = new List<TMP_FontAsset>();
                main.fallbackFontAssetTable = table;
            }

            if (!table.Contains(chinese))
            {
                table.Add(chinese);
            }

            var global = TMP_Settings.fallbackFontAssets;

            if (global != null && !global.Contains(chinese))
            {
                global.Add(chinese);
            }

            main.ReadFontAssetDefinition();
        }
        catch (System.Exception exception)
        {
            HextechPlugin.Log.LogWarning($"挂载中文字体 fallback 失败：{exception.Message}");
        }
    }

    // ── 基础构件 ─────────────────────────────────────────────────
    public static Canvas CreateCanvas(string name, int sortOrder)
    {
        var go = new GameObject(name);
        Object.DontDestroyOnLoad(go);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortOrder;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    public static RectTransform Node(string name, Transform parent)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    public static RectTransform Stretch(RectTransform rect, float left = 0f, float bottom = 0f, float right = 0f, float top = 0f)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
        return rect;
    }

    public static Image Panel(Transform parent, Color color, int radius = 22)
    {
        var rect = Node("Panel", parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = RoundedSprite(radius);
        image.type = Image.Type.Sliced;
        image.color = color;
        return image;
    }

    public static Image Solid(Transform parent, Color color)
    {
        var rect = Node("Solid", parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        return image;
    }

    /// <summary>圆角矩形块，用于卡片、按钮、标签底。</summary>
    public static Image Rounded(Transform parent, Color color, int radius = 16)
    {
        var rect = Node("Rounded", parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = RoundedSprite(radius);
        image.type = Image.Type.Sliced;
        image.color = color;
        return image;
    }

    public static Image Glow(Transform parent, Color color)
    {
        var rect = Node("Glow", parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = GlowSprite();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    public static Image Circle(Transform parent, Color color)
    {
        var rect = Node("Circle", parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = CircleSprite();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    public static Image Ring(Transform parent, Color color)
    {
        var rect = Node("Ring", parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = RingSprite();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    /// <summary>顶部亮、底部透明的竖直渐变，用来给卡片加一层「受光」。</summary>
    public static Image TopSheen(Transform parent, Color color)
    {
        var rect = Node("Sheen", parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = GradientSprite();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    /// <summary>游戏物品图标是 <see cref="Texture2D"/>，所以用 RawImage 直接显示。</summary>
    public static RawImage Icon(Transform parent, Texture2D? texture)
    {
        var rect = Node("Icon", parent);
        var image = rect.gameObject.AddComponent<RawImage>();
        image.texture = texture;
        image.raycastTarget = false;
        return image;
    }

    public static CanvasGroup Group(RectTransform rect)
    {
        var group = rect.gameObject.GetComponent<CanvasGroup>();

        if (group == null)
        {
            group = rect.gameObject.AddComponent<CanvasGroup>();
        }

        group.blocksRaycasts = false;
        group.interactable = false;
        return group;
    }

    public static TextMeshProUGUI Label(
        Transform parent,
        string text,
        float size,
        TextAlignmentOptions alignment,
        Color color)
    {
        var rect = Node("Label", parent);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        label.font = ResolveFont();
        label.text = text;
        label.fontSize = size;
        label.alignment = alignment;
        label.color = color;
        label.textWrappingMode = TextWrappingModes.Normal;
        label.raycastTarget = false;
        Labels.Add(label);
        return label;
    }

    // ── 运行时贴图 ───────────────────────────────────────────────
    public static Sprite RoundedSprite(int radius = 10)
    {
        radius = Mathf.Clamp(radius, 2, 30);

        if (RoundedCache.TryGetValue(radius, out var cached))
        {
            return cached;
        }

        const int size = 64;

        var texture = NewTexture(size);
        var pixels = new Color32[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[(y * size) + x] = White(AlphaAt(x, y, size, radius));
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();

        var border = new Vector4(radius, radius, radius, radius);

        var sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            border);

        RoundedCache[radius] = sprite;
        return sprite;
    }

    /// <summary>中心白、边缘透明的径向渐变，叠在卡片下方当辉光用。</summary>
    public static Sprite GlowSprite()
    {
        if (_glowSprite != null)
        {
            return _glowSprite;
        }

        const int size = 128;
        const float half = (size - 1) * 0.5f;

        var texture = NewTexture(size);
        var pixels = new Color32[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x - half) / half;
                var dy = (y - half) / half;
                var distance = Mathf.Sqrt((dx * dx) + (dy * dy));
                var alpha = Mathf.Clamp01(1f - distance);
                pixels[(y * size) + x] = White(alpha * alpha);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        _glowSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        return _glowSprite;
    }

    public static Sprite CircleSprite()
    {
        if (_circleSprite != null)
        {
            return _circleSprite;
        }

        const int size = 96;
        const float half = (size - 1) * 0.5f;
        const float radius = size * 0.5f;

        var texture = NewTexture(size);
        var pixels = new Color32[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - half;
                var dy = y - half;
                var distance = Mathf.Sqrt((dx * dx) + (dy * dy));
                pixels[(y * size) + x] = White(Mathf.Clamp01(radius - distance));
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        _circleSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        return _circleSprite;
    }

    public static Sprite RingSprite()
    {
        if (_ringSprite != null)
        {
            return _ringSprite;
        }

        const int size = 96;
        const float half = (size - 1) * 0.5f;
        const float outer = size * 0.5f;
        const float thickness = size * 0.09f;

        var texture = NewTexture(size);
        var pixels = new Color32[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - half;
                var dy = y - half;
                var distance = Mathf.Sqrt((dx * dx) + (dy * dy));
                var alpha = Mathf.Clamp01(outer - distance) * Mathf.Clamp01(distance - (outer - thickness));
                pixels[(y * size) + x] = White(alpha);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        _ringSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        return _ringSprite;
    }

    /// <summary>上不透明、下透明的竖直渐变（白色，靠 Image.color 上色）。</summary>
    public static Sprite GradientSprite()
    {
        if (_gradientSprite != null)
        {
            return _gradientSprite;
        }

        const int size = 64;

        var texture = NewTexture(size);
        var pixels = new Color32[size * size];

        for (var y = 0; y < size; y++)
        {
            var alpha = y / (float)(size - 1);

            for (var x = 0; x < size; x++)
            {
                pixels[(y * size) + x] = White(alpha);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        _gradientSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        return _gradientSprite;
    }

    private static Texture2D NewTexture(int size)
    {
        return new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
    }

    private static Color32 White(float alpha)
    {
        return new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255f));
    }

    private static float AlphaAt(int x, int y, int size, int radius)
    {
        var cx = Mathf.Clamp(x, radius, size - 1 - radius);
        var cy = Mathf.Clamp(y, radius, size - 1 - radius);
        var dx = x - cx;
        var dy = y - cy;
        var distance = Mathf.Sqrt((dx * dx) + (dy * dy));
        return Mathf.Clamp01(radius - distance + 0.5f);
    }

    // ── 缓动 ─────────────────────────────────────────────────────
    public static float EaseOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        var inverse = 1f - t;
        return 1f - (inverse * inverse * inverse);
    }

    public static float EaseOutQuint(float t)
    {
        t = Mathf.Clamp01(t);
        var inverse = 1f - t;
        return 1f - (inverse * inverse * inverse * inverse * inverse);
    }

    public static float EaseOutBack(float t)
    {
        t = Mathf.Clamp01(t);
        const float overshoot = 1.7f;
        var shifted = t - 1f;
        return 1f + ((overshoot + 1f) * shifted * shifted * shifted) + (overshoot * shifted * shifted);
    }
}
