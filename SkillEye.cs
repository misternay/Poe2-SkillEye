namespace SkillEye
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameOffsets.Objects.Components;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class SkillEye : PCore<SkillEyeSettings>
    {
        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.json");

        private bool _texturesInitialized;
        private readonly TextureLoader _iconTextureLoader = new();

        private readonly HashSet<string> _lastSeenActiveSkillNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pinnedSkillNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _pinnedSkillIsLocked = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] _trimTags = new[] { "Skill", "Player", "Enemy", "Projectile", "Active" };
        private static readonly HashSet<char> _invalidFileNameCharSet = new(Path.GetInvalidFileNameChars());

        private string _statusMessage = "Ready.";
        private string _selectedSkillName = null;

        // Usage and cooldown tracking keyed by skill name
        private readonly Dictionary<string, (int lastUseCount, bool wasUsable)> _skillUsageStateByName = [];
        private readonly Stopwatch _cooldownClock = Stopwatch.StartNew(); // raw monotonic reference
        private readonly Dictionary<string, double> _cooldownEndMsBySkill = new(StringComparer.OrdinalIgnoreCase);

        // Pausable "game time" derived from _cooldownClock
        private bool _isPaused = false;
        private double _pauseStartRawMs = 0;
        private double _pausedAccumulatedMs = 0;
        private double _gameTimeAtPauseMs = 0;

        private Vector2 _lastGridTargetSize = Vector2.Zero;

        // Best raw entry per skill name (deduped + ranked by metrics richness)
        private readonly Dictionary<string, (int Index,
            ActiveSkillDetails Details,
            IReadOnlyList<(string Label, decimal Value)> Metrics)>
            _bestRowBySkillName = new(StringComparer.OrdinalIgnoreCase);

        public override void OnEnable(bool isGameOpened)
        {
            _statusMessage = "SkillEye loaded.";
            _texturesInitialized = false;

            var serializerSettings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
            try
            {
                if (File.Exists(SettingPathname))
                {
                    var json = File.ReadAllText(SettingPathname);
                    Settings = JsonConvert.DeserializeObject<SkillEyeSettings>(json, serializerSettings) ?? new SkillEyeSettings();
                }
                else
                {
                    Settings = new SkillEyeSettings();
                }
            }
            catch (Exception ex)
            {
                Settings = new SkillEyeSettings();
                _statusMessage = $"[SkillEye] Failed to read settings; using defaults. Error: {ex.Message}";
            }

            Settings.PinnedStatus ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Settings.PinnedStatus = new Dictionary<string, bool>(Settings.PinnedStatus, StringComparer.OrdinalIgnoreCase);

            _pinnedSkillNames.Clear();
            _pinnedSkillNames.UnionWith(Settings.PinnedSkills ?? Enumerable.Empty<string>());

            _pinnedSkillIsLocked.Clear();
            foreach (var pinnedStatus in Settings.PinnedStatus)
                _pinnedSkillIsLocked[pinnedStatus.Key] = pinnedStatus.Value;

            _isPaused = false;
            _pausedAccumulatedMs = 0;
            _pauseStartRawMs = 0;
            _gameTimeAtPauseMs = 0;
        }

        public override void OnDisable()
        {
            _iconTextureLoader.Cleanup();
            _texturesInitialized = false;
            SaveSettings();
        }

        public override void DrawSettings()
        {
            ImGui.TextWrapped("SkillEye: select skills and pin them to the overlay to monitor usability.");

            if (!TryGetPlayerActor(out var playerActor))
            {
                ImGui.Text("No player/actor found (are you in-game?).");
                return;
            }

            // Skill picker
            if (ImGui.BeginCombo("Select Skill", _selectedSkillName ?? "<none>"))
            {
                int pickerIndex = 0;
                foreach (var skillName in _bestRowBySkillName.Keys.OrderBy(n => n ?? string.Empty))
                {
                    var displayName = string.IsNullOrEmpty(skillName) ? "(no name)" : skillName;
                    bool selected = (_selectedSkillName == skillName);
                    if (ImGui.Selectable($"{displayName}##pick_{pickerIndex}", selected))
                        _selectedSkillName = skillName;
                    if (selected) ImGui.SetItemDefaultFocus();
                    pickerIndex++;
                }
                ImGui.EndCombo();
            }

            if (_selectedSkillName != null && ImGui.Button("Pin Selected Skill to Overlay"))
            {
                if (_pinnedSkillNames.Add(_selectedSkillName))
                {
                    _pinnedSkillIsLocked[_selectedSkillName] = false;
                    _statusMessage = $"[SkillEye] Pinned {_selectedSkillName} to overlay.";
                    SaveSettings();
                }
            }

            // Pinned list with unpin buttons
            if (_pinnedSkillNames.Count > 0)
            {
                ImGui.Separator();
                ImGui.Text("Currently pinned skills:");
                foreach (var pinnedSkillName in _pinnedSkillNames.ToList())
                {
                    ImGui.TextUnformatted(pinnedSkillName);
                    ImGui.SameLine();
                    if (ImGui.Button($"Unpin##{pinnedSkillName}"))
                    {
                        _pinnedSkillNames.Remove(pinnedSkillName);
                        _pinnedSkillIsLocked.Remove(pinnedSkillName);
                        _statusMessage = $"[SkillEye] Unpinned {pinnedSkillName}.";
                        SaveSettings();
                    }
                }
            }

            ImGui.Separator();

            // Global opacity
            float opacityPercent = Settings.GlobalOpacity * 100f;
            if (ImGui.SliderFloat("Opacity", ref opacityPercent, 0f, 100f, "%.0f%%"))
                Settings.GlobalOpacity = Clamp01(opacityPercent / 100f);

            if (ImGui.CollapsingHeader("Individual Box"))
            {
                ImGui.Text("Box Layout");
                ImGui.SliderFloat("Icon Padding", ref Settings.IconPadding, 0f, 20f);
                ImGui.SliderFloat("Border Thickness", ref Settings.BorderThickness, 1f, 10f);

                ImGui.Text("Colors");
                ImGui.ColorEdit4("Usable Border", ref Settings.UsableBorder);
                ImGui.ColorEdit4("Not Usable Border", ref Settings.NotUsableBorder);
                ImGui.ColorEdit4("Not Usable Overlay", ref Settings.NotUsableOverlay);
                ImGui.ColorEdit4("Fallback Icon Color", ref Settings.FallbackIconColor);

                ImGui.Text("Text");
                ImGui.ColorEdit4("Skill Text Color", ref Settings.SkillTextColor);
                ImGui.ColorEdit4("Skill Text Outline Color", ref Settings.SkillTextOutlineColor);
                ImGui.SliderFloat("Text Outline Thickness", ref Settings.SkillTextOutlineThickness, 0.5f, 5f);
            }

            ImGui.Separator();

            if (ImGui.CollapsingHeader("Grid System"))
            {
                ImGui.Checkbox("Use Grid (single window)", ref Settings.UseGrid);
                if (ImGui.Checkbox("Grid Locked", ref Settings.GridLocked))
                    _statusMessage = Settings.GridLocked ? "[SkillEye] Grid locked." : "[SkillEye] Grid unlocked.";

                ImGui.SliderInt("Grid Columns", ref Settings.GridColumns, 1, 10);
                ImGui.SliderInt("Grid Tile Size", ref Settings.GridTileSize, 64, 256);
                ImGui.SliderFloat("Grid Gap", ref Settings.GridGap, 0f, 24f);
                ImGui.Checkbox("Show Labels", ref Settings.GridShowLabels);
            }

            ImGui.Separator();
            ImGui.TextWrapped(_statusMessage);
        }

        public override void DrawUI()
        {
            UpdatePauseClock();

            bool isGameHelperForeground = Process.GetCurrentProcess().MainWindowHandle == GetForegroundWindow();
            if (!Core.Process.Foreground && !isGameHelperForeground)
                return;

            if (_pinnedSkillNames.Count == 0)
                return;

            if (!TryGetPlayerActor(out var playerActor))
                return;

            if (Settings.UseGrid)
            {
                DrawGridWindow(playerActor);
            }
            else
            {
                foreach (var pinnedSkillName in _pinnedSkillNames.ToList())
                    DrawSkillOverlay(pinnedSkillName, playerActor);
            }
        }

        public override void SaveSettings()
        {
            try
            {
                Settings.PinnedSkills = new(_pinnedSkillNames);
                Settings.PinnedStatus = new Dictionary<string, bool>(_pinnedSkillIsLocked, StringComparer.OrdinalIgnoreCase);

                var settingsDirectory = Path.GetDirectoryName(SettingPathname);
                if (!string.IsNullOrEmpty(settingsDirectory))
                    Directory.CreateDirectory(settingsDirectory);

                var json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
                File.WriteAllText(SettingPathname, json);
            }
            catch (Exception ex)
            {
                _statusMessage = $"[SkillEye] Failed to save settings: {ex.Message}";
            }
        }

        private void EnsureTexturesLoaded()
        {
            if (_texturesInitialized) return;
            _texturesInitialized = true;
        }

        private bool TryGetPlayerActor(out Actor playerActor)
        {
            playerActor = null;
            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (player == null || !player.TryGetComponent<Actor>(out playerActor))
                return false;

            EnsureTexturesLoaded();
            RefreshSkillsAndIcons(playerActor);
            return true;
        }

        /// <summary>
        ///     Refresh names & icon set
        /// </summary>
        private void RefreshSkillsAndIcons(Actor playerActor)
        {
            // Build (or fetch cached) best rows and mirror names into _bestRowBySkillName (for picker + icons)
            var bestRowsByName = ActiveSkillScanner.GetRowsCached(playerActor, null, forceFull: false);

            _bestRowBySkillName.Clear();
            foreach (var kv in bestRowsByName)
                _bestRowBySkillName[kv.Key] = (kv.Value.Index, kv.Value.Details, kv.Value.Metrics);

            // Detect changes in available skills
            var currentActiveSkillNames = new HashSet<string>(_bestRowBySkillName.Keys, StringComparer.OrdinalIgnoreCase);
            if (!_lastSeenActiveSkillNames.SetEquals(currentActiveSkillNames))
            {
                _lastSeenActiveSkillNames.Clear();
                foreach (var name in currentActiveSkillNames) _lastSeenActiveSkillNames.Add(name);

                if (_selectedSkillName != null && !_lastSeenActiveSkillNames.Contains(_selectedSkillName))
                    _selectedSkillName = null;
            }

            // Compute desired icons (+ lock)
            var desiredIconTextureKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lock.png" };
            foreach (var pinnedSkillName in _pinnedSkillNames)
                desiredIconTextureKeys.Add($"{NormalizeSkillName(pinnedSkillName)}.png");

            _iconTextureLoader.SetDesired(desiredIconTextureKeys);
        }

        private static string NormalizeSkillName(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return string.Empty;

            var trimmed = skillName.Trim();
            bool changed;
            do
            {
                changed = false;
                foreach (var tag in _trimTags)
                {
                    if (trimmed.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
                    {
                        trimmed = trimmed[..^tag.Length].TrimEnd();
                        changed = true;
                        break;
                    }
                }
            } while (changed);

            var withoutWhitespace = new string(trimmed.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
            var safeForFileSystem = new string(withoutWhitespace.Select(c => _invalidFileNameCharSet.Contains(c) ? '_' : c).ToArray());
            return safeForFileSystem;
        }

        private static bool IsCooldownSuppressed(string skillName) =>
            !string.IsNullOrEmpty(skillName) &&
            skillName.Contains("BarragePlayer", StringComparison.OrdinalIgnoreCase);

        private static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);

        private static void AddOutlinedText(ImDrawListPtr drawList, ImFontPtr font, float fontPixelSize, Vector2 position, string text, Vector4 outlineColor, Vector4 textColor, float outlineThicknessPx)
        {
            uint outline = ImGui.GetColorU32(outlineColor);
            drawList.AddText(font, fontPixelSize, new Vector2(position.X - outlineThicknessPx, position.Y), outline, text);
            drawList.AddText(font, fontPixelSize, new Vector2(position.X + outlineThicknessPx, position.Y), outline, text);
            drawList.AddText(font, fontPixelSize, new Vector2(position.X, position.Y - outlineThicknessPx), outline, text);
            drawList.AddText(font, fontPixelSize, new Vector2(position.X, position.Y + outlineThicknessPx), outline, text);
            drawList.AddText(font, fontPixelSize, position, ImGui.GetColorU32(textColor), text);
        }

        private static bool TryFindCooldownMs(IReadOnlyList<(string Label, decimal Value)> metrics, out int cooldownMs)
        {
            if (metrics is not null)
            {
                for (int i = 0; i < metrics.Count; i++)
                {
                    var label = metrics[i].Label;
                    if (label != null && label.Equals("CooldownMs", StringComparison.OrdinalIgnoreCase))
                    {
                        cooldownMs = (int)metrics[i].Value;
                        return true;
                    }
                }
            }
            cooldownMs = 0;
            return false;
        }

        /// <summary>
        ///     Prefer live metrics; fall back to best-row snapshot; then struct field.
        /// </summary>
        private int GetCooldownMsNow(Actor playerActor, string skillName, ActiveSkillDetails activeSkillDetails)
        {
            if (ActiveSkillScanner.TryGetLiveMetrics(playerActor, skillName, out var liveMetrics) &&
                TryFindCooldownMs(liveMetrics, out var liveCooldownMs))
            {
                return liveCooldownMs;
            }

            if (_bestRowBySkillName.TryGetValue(skillName, out var bestRow) &&
                TryFindCooldownMs(bestRow.Metrics, out var cachedCooldownMs))
            {
                return cachedCooldownMs;
            }

            if (activeSkillDetails.TotalCooldownTimeInMs > 0)
                return activeSkillDetails.TotalCooldownTimeInMs;

            return 0;
        }

        private void UpdateUsageAndCooldownState(string skillName, bool isUsable, Actor playerActor,
                                                 ActiveSkillDetails activeSkillDetails,
                                                 (int lastUseCount, bool wasUsable) prior)
        {
            if (activeSkillDetails.TotalUses > prior.lastUseCount)
                _statusMessage = $"[SkillEye] {skillName} used.";
            else if (isUsable != prior.wasUsable)
                _statusMessage = $"[SkillEye] {skillName} usable changed → {isUsable}";

            _skillUsageStateByName[skillName] = (activeSkillDetails.TotalUses, isUsable);

            if (IsCooldownSuppressed(skillName))
            {
                _cooldownEndMsBySkill.Remove(skillName);
                return;
            }

            int cooldownMsNow = GetCooldownMsNow(playerActor, skillName, activeSkillDetails);
            double nowMs = GetGameTimeMs();

            if (prior.wasUsable && !isUsable)
            {
                _cooldownEndMsBySkill[skillName] = nowMs + cooldownMsNow;
            }
            else if (!prior.wasUsable && isUsable)
            {
                _cooldownEndMsBySkill.Remove(skillName);
            }
            else if (!isUsable && cooldownMsNow > 0 && !_cooldownEndMsBySkill.ContainsKey(skillName))
            {
                _cooldownEndMsBySkill[skillName] = nowMs + cooldownMsNow;
            }
        }

        private static double ComputeRemainingCooldownMs(string skillName,
                                                         bool isCurrentlyUsable,
                                                         Func<int> getCooldownMs,
                                                         Func<double> getGameTimeMs,
                                                         IDictionary<string, double> cooldownEndMsBySkill)
        {
            if (isCurrentlyUsable || IsCooldownSuppressed(skillName))
                return 0.0;

            if (cooldownEndMsBySkill.TryGetValue(skillName, out var cooldownEndMs))
            {
                return Math.Max(0.0, cooldownEndMs - getGameTimeMs());
            }

            int cooldownMsNow = getCooldownMs();
            if (cooldownMsNow <= 0)
                return 0.0;

            cooldownEndMsBySkill[skillName] = getGameTimeMs() + cooldownMsNow;
            return cooldownMsNow;
        }

        private void DrawCooldownCentered(ImDrawListPtr drawList, ImFontPtr font,
                                          Vector2 min, Vector2 max,
                                          double remainingMs,
                                          float fontScale,
                                          Vector4 outlineColor, Vector4 textColor)
        {
            if (remainingMs <= 1.0) return;

            int secondsWhole = (int)Math.Floor(remainingMs / 1000.0);
            string text = secondsWhole.ToString(CultureInfo.InvariantCulture) + "s";

            float scaledPixelSize = font.FontSize * fontScale;
            var textSize = ImGui.CalcTextSize(text) * (scaledPixelSize / font.FontSize);
            var center = (min + max) * 0.5f;
            var pos = new Vector2(center.X - textSize.X * 0.5f, center.Y - textSize.Y * 0.5f);

            AddOutlinedText(drawList, font, scaledPixelSize, pos, text, outlineColor, textColor, Math.Max(1f, Settings.SkillTextOutlineThickness));
        }

        private bool DrawLockButton(string idSuffix, bool isLocked, bool interactive, float desiredWidthPx)
        {
            if (!interactive) return false;

            var drawList = ImGui.GetWindowDrawList();
            float globalAlpha = Clamp01(Settings.GlobalOpacity);

            var windowSize = ImGui.GetWindowSize();
            var windowPadding = ImGui.GetStyle().WindowPadding;
            float margin = MathF.Max(2f, Settings.IconPadding * 0.5f);

            var (lockTexture, lockWidth, lockHeight) = _iconTextureLoader.GetTexture("lock.png");
            float width = desiredWidthPx;
            float height = desiredWidthPx;
            if (lockTexture != IntPtr.Zero && lockWidth > 0 && lockHeight > 0)
            {
                float aspectRatio = (float)lockHeight / lockWidth;
                height = desiredWidthPx * aspectRatio;
            }

            var localPosition = new Vector2(
                windowSize.X - windowPadding.X - margin - width,
                MathF.Max(0f, (Settings.IconPadding - height) * 0.5f) + margin
            );

            ImGui.SetCursorPos(localPosition);
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(width, height);
            var backgroundMin = min - new Vector2(2f, 2f);
            var backgroundMax = max + new Vector2(2f, 2f);

            uint backgroundColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, (isLocked ? 0.60f : 0.35f) * globalAlpha));
            drawList.AddRectFilled(backgroundMin, backgroundMax, backgroundColor, 4f);

            uint tint = ImGui.GetColorU32(isLocked
                ? new Vector4(1f, 1f, 1f, 0.95f * globalAlpha)
                : new Vector4(1f, 1f, 1f, 0.60f * globalAlpha));

            if (lockTexture != IntPtr.Zero)
                drawList.AddImage(lockTexture, min, max, Vector2.Zero, Vector2.One, tint);
            else
                drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f * globalAlpha)), 3f);

            bool clicked = false;
            ImGui.InvisibleButton($"##lockbtn_{idSuffix}", new Vector2(width, height));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(isLocked ? "Unlock" : "Lock");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                clicked = true;

            return clicked;
        }

        private void DrawSkillOverlay(string skillName, Actor playerActor)
        {
            if (!playerActor.ActiveSkills.TryGetValue(skillName, out var activeSkillDetails))
                return;

            bool isUsable = playerActor.IsSkillUsable.Contains(skillName);

            if (!_skillUsageStateByName.TryGetValue(skillName, out var priorUsageState))
                priorUsageState = (activeSkillDetails.TotalUses, isUsable);

            UpdateUsageAndCooldownState(skillName, isUsable, playerActor, activeSkillDetails, priorUsageState);

            bool isLocked = _pinnedSkillIsLocked.TryGetValue(skillName, out var lockedValue) && lockedValue;
            bool overrideInputs = isLocked && IsAltDown();

            var windowFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            if (isLocked && !overrideInputs)
                windowFlags |= ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;

            var iconTextureKey = $"{NormalizeSkillName(skillName)}.png";
            var (textureHandle, textureWidth, textureHeight) = _iconTextureLoader.GetTexture(iconTextureKey);
            if (textureHandle == IntPtr.Zero)
            {
                _statusMessage = $"[SkillEye] Icon not found for '{skillName}' → expected '{iconTextureKey}' under resources/skillicons or resources.";
                textureWidth = textureHeight = Settings.DefaultIconSize;
            }

            var defaultWindowSize = new Vector2(
                textureWidth + 2 * Settings.IconPadding,
                textureHeight + 2 * Settings.IconPadding
            );

            float globalAlpha = Clamp01(Settings.GlobalOpacity);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var windowBackgroundColor = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
            windowBackgroundColor.W *= globalAlpha;
            ImGui.PushStyleColor(ImGuiCol.WindowBg, windowBackgroundColor);

            ImGui.SetNextWindowSize(defaultWindowSize, ImGuiCond.FirstUseEver);

            if (!ImGui.Begin($"SkillEye Overlay##{skillName}", windowFlags))
            {
                ImGui.End();
                ImGui.PopStyleColor();
                ImGui.PopStyleVar();
                return;
            }

            // Enforce square for nicer resizes
            if (!isLocked || overrideInputs)
            {
                var currentSize = ImGui.GetWindowSize();
                if (Math.Abs(currentSize.X - currentSize.Y) > 0.5f)
                {
                    float side = Math.Max(currentSize.X, currentSize.Y);
                    ImGui.SetWindowSize(new Vector2(side, side));
                }
            }

            var drawList = ImGui.GetWindowDrawList();
            var windowPosition = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            float squareSideSize = windowSize.X;

            var iconDrawSize = new Vector2(
                squareSideSize - 2 * Settings.IconPadding,
                squareSideSize - 2 * Settings.IconPadding
            );

            ImGui.SetCursorPos(new Vector2(Settings.IconPadding, Settings.IconPadding));

            Vector2 iconAreaMin, iconAreaMax;
            if (textureHandle != IntPtr.Zero)
            {
                ImGui.Image(textureHandle, iconDrawSize, Vector2.Zero, Vector2.One, new Vector4(1f, 1f, 1f, globalAlpha), Vector4.Zero);
                if (!isUsable)
                {
                    var itemMin = ImGui.GetItemRectMin();
                    var itemMax = ImGui.GetItemRectMax();
                    var overlayColor = Settings.NotUsableOverlay; overlayColor.W = MathF.Min(overlayColor.W, 0.5f) * globalAlpha;
                    drawList.AddRectFilled(itemMin, itemMax, ImGui.GetColorU32(overlayColor));
                }
                iconAreaMin = ImGui.GetItemRectMin();
                iconAreaMax = ImGui.GetItemRectMax();
            }
            else
            {
                var rectMin = ImGui.GetCursorScreenPos();
                var rectMax = rectMin + iconDrawSize;
                var fallbackColor = Settings.FallbackIconColor; fallbackColor.W *= globalAlpha;
                drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(fallbackColor));
                ImGui.Dummy(iconDrawSize);
                iconAreaMin = rectMin;
                iconAreaMax = rectMax;
            }

            List<(string Label, decimal Value)> metricsList = null;
            if (!ActiveSkillScanner.TryGetLiveMetrics(playerActor, skillName, out metricsList))
            {
                if (_bestRowBySkillName.TryGetValue(skillName, out var bestRowCached) && bestRowCached.Metrics?.Count > 0)
                    metricsList = new List<(string, decimal)>(bestRowCached.Metrics);
            }

            if (metricsList != null)
            {
                var font = ImGui.GetFont();
                float uiScale = Math.Max(0.6f, Math.Min(1.4f, squareSideSize / 140f));
                float metricFontPixelSize = font.FontSize * (uiScale * 0.9f);
                var outlineColor = Settings.SkillTextOutlineColor; outlineColor.W *= globalAlpha;
                var textColor = Settings.SkillTextColor; textColor.W *= globalAlpha;

                var textCursor = iconAreaMin + new Vector2(2f, 2f);
                foreach (var (label, value) in metricsList)
                {
                    if (label != null && label.Equals("CooldownMs", StringComparison.OrdinalIgnoreCase))
                        continue; // cooldown stays as the centered timer

                    string line = $"{label}: {FormatShort(value)}";
                    AddOutlinedText(ImGui.GetWindowDrawList(), font, metricFontPixelSize, textCursor, line, outlineColor, textColor, Math.Max(1f, Settings.SkillTextOutlineThickness));
                    var lineSize = ImGui.CalcTextSize(line) * (metricFontPixelSize / font.FontSize);
                    textCursor.Y += lineSize.Y * 0.95f;
                }
            }

            // Cooldown display (uses pausable clock) — seconds-only
            if (!isUsable && !IsCooldownSuppressed(skillName))
            {
                double remainingMs = ComputeRemainingCooldownMs(
                    skillName,
                    isUsable,
                    () => GetCooldownMsNow(playerActor, skillName, activeSkillDetails),
                    GetGameTimeMs,
                    _cooldownEndMsBySkill);

                if (remainingMs > 1.0)
                {
                    var font = ImGui.GetFont();
                    float uiScale = Math.Max(0.6f, Math.Min(1.4f, squareSideSize / 140f));
                    var outlineColor = Settings.SkillTextOutlineColor; outlineColor.W *= globalAlpha;
                    var textColor = Settings.SkillTextColor; textColor.W *= globalAlpha;
                    DrawCooldownCentered(drawList, font, iconAreaMin, iconAreaMax, remainingMs, uiScale * 1.5f, outlineColor, textColor);
                }
            }

            // Border
            {
                var borderColor = isUsable ? Settings.UsableBorder : Settings.NotUsableBorder;
                borderColor.W *= globalAlpha;
                drawList.AddRect(windowPosition, windowPosition + windowSize, ImGui.GetColorU32(borderColor),
                    0, ImDrawFlags.None, Settings.BorderThickness);
            }

            // Label at bottom
            {
                var font = ImGui.GetFont();
                float baseFontPx = font.FontSize;
                float uiScale = Math.Max(0.6f, Math.Min(1.4f, squareSideSize / 140f));
                float scaledFontPx = baseFontPx * uiScale;

                var baseLabelSize = ImGui.CalcTextSize(skillName);
                var scaledLabelSize = baseLabelSize * uiScale;

                var labelPosition = new Vector2(
                    windowPosition.X + Settings.IconPadding,
                    windowPosition.Y + windowSize.Y - scaledLabelSize.Y - Settings.IconPadding * 0.5f
                );

                var outlineColor = Settings.SkillTextOutlineColor; outlineColor.W *= globalAlpha;
                var textColor = Settings.SkillTextColor; textColor.W *= globalAlpha;

                AddOutlinedText(drawList, font, scaledFontPx, labelPosition, skillName, outlineColor, textColor, Math.Max(1f, Settings.SkillTextOutlineThickness));
            }

            bool interactive = !isLocked || overrideInputs;
            if (interactive && DrawLockButton($"skill_{skillName}", isLocked, true, Settings.LockButtonSize))
            {
                bool newLockedValue = !isLocked;
                _pinnedSkillIsLocked[skillName] = newLockedValue;
                _statusMessage = newLockedValue ? $"[SkillEye] Locked {skillName}." : $"[SkillEye] Unlocked {skillName}.";
                SaveSettings();
            }

            ImGui.End();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
        }

        private void DrawGridWindow(Actor playerActor)
        {
            bool overrideInputs = Settings.GridLocked && IsAltDown();

            var windowFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoResize;
            if (Settings.GridLocked && !overrideInputs)
                windowFlags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs;

            int columns = Math.Max(1, Settings.GridColumns);
            int tileSide = Math.Max(32, Settings.GridTileSize);
            float gap = Math.Max(0f, Settings.GridGap);

            int count = _pinnedSkillNames.Count;
            int rows = Math.Max(1, (count + columns - 1) / columns);

            float innerWidth = columns * tileSide + (columns - 1) * gap;
            float innerHeight = rows * tileSide + (rows - 1) * gap;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            float globalAlpha = Clamp01(Settings.GlobalOpacity);
            var windowBackgroundColor = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
            windowBackgroundColor.W *= globalAlpha;
            ImGui.PushStyleColor(ImGuiCol.WindowBg, windowBackgroundColor);

            var targetSize = new Vector2(innerWidth + 2 * Settings.IconPadding, innerHeight + 2 * Settings.IconPadding);
            if (Math.Abs(targetSize.X - _lastGridTargetSize.X) > 0.5f || Math.Abs(targetSize.Y - _lastGridTargetSize.Y) > 0.5f)
            {
                ImGui.SetNextWindowSize(targetSize, ImGuiCond.Always);
                _lastGridTargetSize = targetSize;
            }

            if (!ImGui.Begin(Settings.GridWindowName, windowFlags))
            {
                ImGui.End();
                ImGui.PopStyleColor();
                ImGui.PopStyleVar();
                return;
            }

            bool gridInteractive = !Settings.GridLocked || overrideInputs;
            Vector2 lockMin = Vector2.Zero, lockMax = Vector2.Zero;
            bool lockClicked = false;

            float margin = MathF.Max(2f, Settings.IconPadding * 0.5f);
            var windowSize = ImGui.GetWindowSize();
            var stylePadding = ImGui.GetStyle().WindowPadding;

            float lockWidthPx = Settings.LockButtonSize;
            float lockHeightPx = Settings.LockButtonSize;

            var (lockTexture, lockTextureWidth, lockTextureHeight) = _iconTextureLoader.GetTexture("lock.png");
            if (lockTexture != IntPtr.Zero && lockTextureWidth > 0 && lockTextureHeight > 0)
                lockHeightPx = lockWidthPx * ((float)lockTextureHeight / lockTextureWidth);

            var lockLocalPosition = new Vector2(
                windowSize.X - stylePadding.X - margin - lockWidthPx,
                MathF.Max(0f, (Settings.IconPadding - lockHeightPx) * 0.5f) + margin
            );

            if (gridInteractive)
            {
                ImGui.SetCursorPos(lockLocalPosition);
                ImGui.InvisibleButton("##lockbtn_grid", new Vector2(lockWidthPx, lockHeightPx));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Settings.GridLocked ? "Unlock grid" : "Lock grid");
                lockClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
                lockMin = ImGui.GetItemRectMin();
                lockMax = ImGui.GetItemRectMax();
            }

            ImGui.SetCursorPos(new Vector2(Settings.IconPadding, Settings.IconPadding));
            ImGui.InvisibleButton("##grid-size-reserve", new Vector2(innerWidth, innerHeight));
            var gridTopLeft = ImGui.GetItemRectMin();

            if (!Settings.GridLocked || overrideInputs)
            {
                if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                    ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
            }

            var drawList = ImGui.GetWindowDrawList();
            var font = ImGui.GetFont();
            float baseFontPx = font.FontSize;

            float uiScale = Math.Max(0.6f, Math.Min(1.4f, tileSide / 140f));
            float scaledFontPx = baseFontPx * uiScale;
            float padding = Settings.IconPadding;
            float borderThickness = Settings.BorderThickness;

            var outlineColorBase = Settings.SkillTextOutlineColor; outlineColorBase.W *= globalAlpha;
            var textColorBase = Settings.SkillTextColor; textColorBase.W *= globalAlpha;
            uint notUsableBorderColor = ImGui.GetColorU32(Settings.NotUsableBorder);
            float outlinePx = Math.Max(1f, Settings.SkillTextOutlineThickness);

            int index = 0;
            foreach (var skillName in _pinnedSkillNames.ToList())
            {
                int row = index / columns;
                int col = index % columns;

                var tileMin = gridTopLeft + new Vector2(col * (tileSide + gap), row * (tileSide + gap));
                var tileMax = tileMin + new Vector2(tileSide, tileSide);

                if (!playerActor.ActiveSkills.TryGetValue(skillName, out var activeSkillDetails))
                {
                    drawList.AddRect(tileMin, tileMax, notUsableBorderColor, 0, ImDrawFlags.None, borderThickness);
                    index++;
                    continue;
                }

                bool isUsable = playerActor.IsSkillUsable.Contains(skillName);

                if (!_skillUsageStateByName.TryGetValue(skillName, out var prior))
                    prior = (activeSkillDetails.TotalUses, isUsable);

                UpdateUsageAndCooldownState(skillName, isUsable, playerActor, activeSkillDetails, prior);

                string iconKey = $"{NormalizeSkillName(skillName)}.png";
                var (textureHandle, textureWidth, textureHeight) = _iconTextureLoader.GetTexture(iconKey);
                if (textureHandle == IntPtr.Zero)
                {
                    textureWidth = textureHeight = Settings.DefaultIconSize;
                }

                var iconMin = tileMin + new Vector2(padding, padding);
                var iconMax = tileMax - new Vector2(padding, padding);

                if (textureHandle != IntPtr.Zero)
                {
                    drawList.AddImage(textureHandle, iconMin, iconMax, Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, globalAlpha)));
                    if (!isUsable)
                    {
                        var overlayColor = Settings.NotUsableOverlay; overlayColor.W = MathF.Min(overlayColor.W, 0.5f) * globalAlpha;
                        drawList.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(overlayColor));
                    }
                }
                else
                {
                    var fallbackColor = Settings.FallbackIconColor; fallbackColor.W *= globalAlpha;
                    drawList.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(fallbackColor));
                }

                var borderColor = isUsable ? Settings.UsableBorder : Settings.NotUsableBorder;
                borderColor.W *= globalAlpha;
                drawList.AddRect(tileMin, tileMax, ImGui.GetColorU32(borderColor), 0, ImDrawFlags.None, borderThickness);

                List<(string Label, decimal Value)> metricsList = null;
                if (!ActiveSkillScanner.TryGetLiveMetrics(playerActor, skillName, out metricsList))
                {
                    if (_bestRowBySkillName.TryGetValue(skillName, out var bestRow) && bestRow.Metrics?.Count > 0)
                        metricsList = new List<(string, decimal)>(bestRow.Metrics);
                }

                if (!isUsable && !IsCooldownSuppressed(skillName))
                {
                    double remainingMs = ComputeRemainingCooldownMs(
                        skillName,
                        isUsable,
                        () => GetCooldownMsNow(playerActor, skillName, activeSkillDetails),
                        GetGameTimeMs,
                        _cooldownEndMsBySkill);

                    if (remainingMs > 1.0)
                    {
                        DrawCooldownCentered(drawList, font, iconMin, iconMax, remainingMs, uiScale * 1.5f, outlineColorBase, textColorBase);
                    }
                }

                if (metricsList != null)
                {
                    float metricFontPx = scaledFontPx * 0.9f;
                    var textCursor = iconMin + new Vector2(2f, 2f);
                    int lines = 0;
                    foreach (var (label, value) in metricsList)
                    {
                        if (label != null && label.Equals("CooldownMs", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string line = $"{label}: {FormatShort(value)}";
                        AddOutlinedText(drawList, font, metricFontPx, textCursor, line, outlineColorBase, textColorBase, outlinePx);
                        var lineSize = ImGui.CalcTextSize(line) * (metricFontPx / font.FontSize);
                        textCursor.Y += lineSize.Y * 0.95f;
                        if (++lines >= 3) break; // keep grid readable
                    }
                }

                if (Settings.GridShowLabels)
                {
                    var baseLabelSize = ImGui.CalcTextSize(skillName);
                    var scaledLabel = baseLabelSize * uiScale;
                    var labelPos = new Vector2(tileMin.X + padding, tileMax.Y - scaledLabel.Y - padding * 0.5f);
                    AddOutlinedText(drawList, font, scaledFontPx, labelPos, skillName, outlineColorBase, textColorBase, outlinePx);
                }

                if (!Settings.GridLocked || overrideInputs)
                {
                    ImGui.SetCursorScreenPos(tileMin);
                    ImGui.InvisibleButton($"##tilehitbox_{skillName}", new Vector2(tileSide, tileSide));
                    if (ImGui.BeginPopupContextItem($"##tilemenu_{skillName}"))
                    {
                        if (ImGui.MenuItem("Unpin"))
                        {
                            _pinnedSkillNames.Remove(skillName);
                            _pinnedSkillIsLocked.Remove(skillName);
                        }
                        ImGui.EndPopup();
                    }
                }

                index++;
            }

            if (gridInteractive)
            {
                var drawListWindow = ImGui.GetWindowDrawList();
                var bgMin = lockMin - new Vector2(2f, 2f);
                var bgMax = lockMax + new Vector2(2f, 2f);
                uint bgColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, (Settings.GridLocked ? 0.60f : 0.35f) * globalAlpha));
                drawListWindow.AddRectFilled(bgMin, bgMax, bgColor, 4f);

                if (lockTexture != IntPtr.Zero)
                {
                    uint tint = ImGui.GetColorU32(Settings.GridLocked
                        ? new Vector4(1f, 1f, 1f, 0.95f * globalAlpha)
                        : new Vector4(1f, 1f, 1f, 0.60f * globalAlpha));
                    drawListWindow.AddImage(lockTexture, lockMin, lockMax, Vector2.Zero, Vector2.One, tint);
                }
                else
                {
                    drawListWindow.AddRect(lockMin, lockMax, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f * globalAlpha)), 3f, 0, 2f);
                }

                if (lockClicked)
                {
                    Settings.GridLocked = !Settings.GridLocked;
                    _statusMessage = Settings.GridLocked ? "[SkillEye] Grid locked." : "[SkillEye] Grid unlocked.";
                    SaveSettings();
                }
            }

            ImGui.End();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
        }

        private static bool IsAltDown()
        {
            return ImGui.IsKeyDown(ImGuiKey.LeftAlt) ||
                   ImGui.IsKeyDown(ImGuiKey.RightAlt);
        }

        private void UpdatePauseClock()
        {
            var state = Core.States.GameCurrentState;
            double rawNowMs = _cooldownClock.Elapsed.TotalMilliseconds;

            if (state == GameStateTypes.EscapeState)
            {
                if (!_isPaused)
                {
                    _isPaused = true;
                    _pauseStartRawMs = rawNowMs;
                    _gameTimeAtPauseMs = rawNowMs - _pausedAccumulatedMs;
                }
            }
            else
            {
                if (_isPaused)
                {
                    _pausedAccumulatedMs += Math.Max(0, rawNowMs - _pauseStartRawMs);
                    _pauseStartRawMs = 0;
                    _isPaused = false;
                }
            }
        }

        private double GetGameTimeMs()
        {
            double rawMs = _cooldownClock.Elapsed.TotalMilliseconds;
            return _isPaused ? _gameTimeAtPauseMs : (rawMs - _pausedAccumulatedMs);
        }

        private static string FormatShort(decimal value)
        {
            if (value > 999_999_999m) return value.ToString("0,,,.###B", CultureInfo.InvariantCulture);
            if (value > 999_999m) return value.ToString("0,,.##M", CultureInfo.InvariantCulture);
            if (value > 999m) return value.ToString("0,.##K", CultureInfo.InvariantCulture);
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();
    }
}