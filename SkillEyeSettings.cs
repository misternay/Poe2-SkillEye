namespace SkillEye
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.Plugin;

    /// <summary>
    ///     User-configurable settings for SkillEye. All values have safe defaults.
    /// </summary>
    public sealed class SkillEyeSettings : IPSettings
    {
        // Persisted skill pins (+ locked state)
        public List<string> PinnedSkills = new();
        public Dictionary<string, bool> PinnedStatus = new(StringComparer.OrdinalIgnoreCase);

        // Global
        public float GlobalOpacity = 1.0f;

        // Individual boxes
        public float IconPadding = 4.0f;
        public float BorderThickness = 2.0f;
        public int DefaultIconSize = 64;
        public float LockButtonSize = 18.0f;

        // Colors
        public Vector4 UsableBorder = new(0.31f, 0.78f, 0.47f, 1.0f);
        public Vector4 NotUsableBorder = new(0.90f, 0.25f, 0.25f, 1.0f);
        public Vector4 NotUsableOverlay = new(0f, 0f, 0f, 0.35f);
        public Vector4 FallbackIconColor = new(0.2f, 0.2f, 0.2f, 1.0f);

        // Text
        public Vector4 SkillTextColor = new(1f, 1f, 1f, 1.0f);
        public Vector4 SkillTextOutlineColor = new(0f, 0f, 0f, 1.0f);
        public float SkillTextOutlineThickness = 1.25f;

        // Grid
        public bool UseGrid = false;
        public bool GridLocked = true;
        public int GridColumns = 4;
        public int GridTileSize = 128; // pixel side
        public float GridGap = 4.0f;
        public bool GridShowLabels = true;
        public string GridWindowName = "SkillEye Grid";
    }
}