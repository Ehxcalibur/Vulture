using System;
using BepInEx.Configuration;
using Luc1dShadow.Vulture.Integration; // Add namespace
using UnityEngine;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Custom in-game GUI for Vulture configuration.
    /// Activated by F7 key (configurable), uses Void & Violet theme.
    /// </summary>
    public class VultureGUI : MonoBehaviour
    {
        private bool _showGUI = false;
        private int _currentTab = 0;
        private Rect _windowRect;
        private Vector2 _scrollPosition;
        private bool _stylesInitialized = false;
        
        // Tab names
        private readonly string[] _tabs = { "General", "Triggers", "Behaviors", "Advanced", "Maps", "Integrations" };
        
        // Window dimensions
        private const float WINDOW_WIDTH = 500f;
        private const float WINDOW_HEIGHT = 450f;
        private const float HEADER_HEIGHT = 35f;
        private const float TAB_HEIGHT = 30f;
        private const float PADDING = 10f;
        
        // Void & Violet Theme Colors - Dark Edition
        private static readonly Color BgDark = new Color(0.04f, 0.03f, 0.06f);      // #0A080F - Near black
        private static readonly Color BgMid = new Color(0.08f, 0.06f, 0.12f);       // #140F1E - Very dark purple
        private static readonly Color BgLight = new Color(0.12f, 0.08f, 0.18f);     // #1F142E - Dark purple
        private static readonly Color Accent = new Color(0.45f, 0.18f, 0.70f);      // #732DB3 - Deep violet
        private static readonly Color Highlight = new Color(0.58f, 0.30f, 0.85f);   // #944DD9 - Brighter violet
        private static readonly Color Secondary = new Color(0.30f, 0.10f, 0.50f);   // #4D1A80 - Dark purple header
        private static readonly Color TextLight = new Color(0.78f, 0.60f, 0.92f);   // #C799EB - Soft lavender
        private static readonly Color TextDim = new Color(0.45f, 0.35f, 0.55f);     // #73598C - Muted purple
        
        // Styles
        private GUIStyle _windowStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _tabStyle;
        private GUIStyle _tabActiveStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _toggleStyle;
        private GUIStyle _sliderThumbStyle;
        private GUIStyle _boxStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _closeButtonStyle;
        private GUIStyle _resetButtonStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _tooltipStyle;
        
        // Textures
        private Texture2D _bgDarkTex;
        private Texture2D _bgMidTex;
        private Texture2D _bgLightTex;
        private Texture2D _accentTex;
        private Texture2D _highlightTex;
        private Texture2D _secondaryTex;
        
        // Cursor state tracking
        private bool _cursorWasLocked;
        private bool _cursorWasVisible;
        
        void Start()
        {
            _windowRect = new Rect(
                (Screen.width - WINDOW_WIDTH) / 2,
                (Screen.height - WINDOW_HEIGHT) / 2,
                WINDOW_WIDTH,
                WINDOW_HEIGHT
            );
        }
        
        void Update()
        {
            // Check for toggle key
            if (Input.GetKeyDown(Plugin.GUIKey.Value))
            {
                ToggleGUI();
            }
            
            // Also allow Escape to close
            if (_showGUI && Input.GetKeyDown(KeyCode.Escape))
            {
                ToggleGUI();
            }
        }
        
        private void ToggleGUI()
        {
            _showGUI = !_showGUI;
            
            if (_showGUI)
            {
                // Store cursor state
                _cursorWasLocked = Cursor.lockState == CursorLockMode.Locked;
                _cursorWasVisible = Cursor.visible;
                
                // Unlock cursor
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                // Restore cursor state
                if (_cursorWasLocked)
                    Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = _cursorWasVisible;
            }
        }
        
        void OnGUI()
        {
            if (!_showGUI) return;
            
            // Initialize styles on first GUI call
            if (!_stylesInitialized)
            {
                InitializeStyles();
                _stylesInitialized = true;
            }
            
            // Keep cursor unlocked while GUI is open
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            
            // Draw window
            _windowRect = GUI.Window(9999, _windowRect, DrawWindow, "", _windowStyle);
            
            // Consume input
            if (_windowRect.Contains(Event.current.mousePosition))
            {
                UnityEngine.Input.ResetInputAxes();
            }
        }
        
        private void DrawWindow(int windowID)
        {
            // Header bar
            GUI.Box(new Rect(0, 0, _windowRect.width, HEADER_HEIGHT), "", _headerStyle);
            GUI.Label(new Rect(PADDING, 5, 300, 25), "VULTURE CONFIG", _headerStyle);
            
            // Close button
            if (GUI.Button(new Rect(_windowRect.width - 35, 5, 25, 25), "X", _closeButtonStyle))
            {
                ToggleGUI();
            }
            
            // Reset to Defaults button
            if (GUI.Button(new Rect(_windowRect.width - 110, 5, 70, 25), "Reset", _resetButtonStyle))
            {
                ResetAllToDefaults();
            }
            
            // Tab bar
            float tabWidth = (_windowRect.width - PADDING * 2) / _tabs.Length;
            for (int i = 0; i < _tabs.Length; i++)
            {
                Rect tabRect = new Rect(PADDING + i * tabWidth, HEADER_HEIGHT + 5, tabWidth - 2, TAB_HEIGHT);
                GUIStyle style = (i == _currentTab) ? _tabActiveStyle : _tabStyle;
                if (GUI.Button(tabRect, _tabs[i], style))
                {
                    _currentTab = i;
                }
            }
            
            // Content area
            float contentY = HEADER_HEIGHT + TAB_HEIGHT + 15;
            float footerHeight = 40f;
            float contentHeight = _windowRect.height - contentY - PADDING - footerHeight - 5;
            Rect contentRect = new Rect(PADDING, contentY, _windowRect.width - PADDING * 2, contentHeight);
            
            GUI.Box(contentRect, "", _boxStyle);
            
            GUILayout.BeginArea(new Rect(contentRect.x + 5, contentRect.y + 5, contentRect.width - 10, contentRect.height - 10));
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);
            
            switch (_currentTab)
            {
                case 0: DrawGeneralTab(); break;
                case 1: DrawTriggersTab(); break;
                case 2: DrawBehaviorsTab(); break;
                case 3: DrawAdvancedTab(); break;
                case 4: DrawMapsTab(); break;
                case 5: DrawIntegrationsTab(); break;
            }
            
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            
            // Tooltip footer
            Rect footerRect = new Rect(PADDING, contentY + contentHeight + 5, _windowRect.width - PADDING * 2, footerHeight);
            GUI.Box(footerRect, "", _boxStyle);
            
            string tooltip = GUI.tooltip;
            if (!string.IsNullOrEmpty(tooltip))
            {
                 GUI.Label(new Rect(footerRect.x + 5, footerRect.y + 5, footerRect.width - 10, footerRect.height - 10), tooltip, _tooltipStyle);
            }
            else
            {
                 GUI.Label(new Rect(footerRect.x + 5, footerRect.y + 5, footerRect.width - 10, footerRect.height - 10), "Hover over an option to see details.", _tooltipStyle);
            }
            
            // Make window draggable
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, HEADER_HEIGHT));
        }
        
        #region Tab Content
        
        private void DrawGeneralTab()
        {
            DrawSection("Logging");
            DrawToggle("Debug Logging", Plugin.DebugLogging, "Enable detailed debug logs in BepInEx console");
            
            GUILayout.Space(15);
            
            DrawSection("Bot Roles");
            DrawToggle("Enable PMCs", Plugin.EnablePMCs, "Allow USEC/BEAR bots to vulture");
            DrawToggle("Enable Scavs", Plugin.EnableScavs, "Allow AI Scavs to vulture");
            DrawToggle("Enable Player Scavs", Plugin.EnablePScavs, "Allow Player Scavs to vulture");
        }
        
        private void DrawTriggersTab()
        {
            DrawSection("Detection");
            DrawSliderInt("Vulture Chance", Plugin.VultureChance, 0, 100, "%", $"Base chance to investigate a shot. Default: {Plugin.VultureChance.DefaultValue}%");
            DrawSliderFloat("Base Detection Range", Plugin.BaseDetectionRange, 50f, 500f, "m", $"Max distance to react to sounds (before map multiplier). Default: 150m");
            DrawSliderInt("Min Squad Size", Plugin.MinSquadSize, 1, 5, "", $"Minimum squad size to activate. Default: {Plugin.MinSquadSize.DefaultValue}");
            DrawToggle("Detect Explosions", Plugin.EnableExplosionDetection, "Also react to grenade explosions");
            
            GUILayout.Space(15);
            
            DrawSection("Multi-Shot Intensity");
            DrawSliderInt("Intensity Bonus", Plugin.MultiShotIntensity, 0, 20, "%", "Bonus chance per extra shot in area");
            DrawSliderFloat("Intensity Window", Plugin.IntensityWindow, 5f, 60f, "s", "Time window for counting shots");
            
            GUILayout.Space(15);
            
            DrawSection("Dynamic Courage (Fear)");
            DrawSliderInt("Courage Threshold", Plugin.CourageThreshold, 0, 30, "", "Max shots/explosions in 5s before bot hesitates. Lower = more cautious.");
        }
        
        private void DrawBehaviorsTab()
        {
            DrawSection("Ambush");
            DrawSliderFloat("Ambush Duration", Plugin.AmbushDuration, 30f, 180f, "s", "How long to hold position");
            DrawSliderFloat("Ambush Min Distance", Plugin.AmbushDistanceMin, 10f, 50f, "m", "Minimum distance from target");
            DrawSliderFloat("Ambush Max Distance", Plugin.AmbushDistanceMax, 20f, 75f, "m", "Maximum distance from target");
            DrawSliderFloat("Silence Trigger (Rush)", Plugin.SilenceTriggerDuration, 15f, 120f, "s", "If no shots for this long, switch from creep to rush");
            
            GUILayout.Space(15);
            
            DrawSection("Post-Ambush");
            DrawToggle("Greed", Plugin.LootGreed, "Push aggressively toward combat zone after ambush timer expires");
            DrawToggle("Squad Coordination", Plugin.SquadCoordination, "Followers join when leader vultures");
            
            GUILayout.Space(15);
            
            DrawSection("Smart Tactics");
            DrawToggle("Smart Ambush (Choke Points)", Plugin.SmartAmbush, "Use cover/corners for ambush instead of open ground");
            DrawToggle("Baiting", Plugin.EnableBaiting, "Fire decoy shots to lure enemies");
            if (Plugin.EnableBaiting.Value)
            {
                DrawSliderInt("Baiting Chance", Plugin.BaitingChance, 0, 100, "%", "Chance to fire bait shot while waiting");
            }
        }
        
        private void DrawAdvancedTab()
        {
            DrawSection("Immersion");
            DrawToggle("Voice Lines", Plugin.EnableVoiceLines, "Bots whisper/talk when entering Vulture mode");
            DrawToggle("Flashlight Discipline", Plugin.FlashlightDiscipline, "Bots turn off lights/lasers when stalking");

            GUILayout.Space(15);

            DrawSection("Movement");
            DrawToggle("Silent Approach", Plugin.SilentApproach, "Crouch-walk to ambush point");
            DrawSliderFloat("Silent Distance", Plugin.SilentApproachDistance, 10f, 100f, "m", "Distance to start creeping");
            DrawToggle("Paranoia", Plugin.Paranoia, "Random head swivels while ambushing");
            
            GUILayout.Space(15);
            
            DrawSection("Boss Avoidance");
            DrawToggle("Enable Boss Avoidance", Plugin.BossAvoidance, "Avoid vulturing toward boss fights");
            DrawSliderFloat("Avoidance Radius", Plugin.BossAvoidanceRadius, 25f, 200f, "m", "Radius around boss activity");
            DrawSliderFloat("Zone Decay", Plugin.BossZoneDecay, 30f, 300f, "s", "How long zones stay 'hot'");
            
            GUILayout.Space(15);
            
            DrawSection("Time of Day");
            DrawToggle("Night Modifier", Plugin.NightTimeModifier, "Reduce range at night (harder to locate)");
            DrawSliderFloat("Night Multiplier", Plugin.NightRangeMultiplier, 0.2f, 1f, "x", "Range multiplier during night");
        }
        
        private void DrawMapsTab()
        {
            GUILayout.Label($"Base Detection Range: {Plugin.BaseDetectionRange.Value:F0}m", _labelStyle);
            GUILayout.Label("Multipliers scale the base detection range for each map.", _tooltipStyle);
            GUILayout.Space(10);

            DrawSection("Indoor Maps");
            DrawSliderFloat("Factory", Plugin.FactoryMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.FactoryMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.FactoryMultiplier.Value:F0}m effective range");
            DrawSliderFloat("Labs", Plugin.LabsMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.LabsMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.LabsMultiplier.Value:F0}m effective range");
            DrawSliderFloat("Interchange", Plugin.InterchangeMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.InterchangeMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.InterchangeMultiplier.Value:F0}m effective range");
            
            GUILayout.Space(15);
            
            DrawSection("Mixed Maps");
            DrawSliderFloat("Reserve", Plugin.ReserveMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.ReserveMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.ReserveMultiplier.Value:F0}m effective range");
            DrawSliderFloat("Customs", Plugin.CustomsMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.CustomsMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.CustomsMultiplier.Value:F0}m effective range");
            DrawSliderFloat("Ground Zero", Plugin.GroundZeroMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.GroundZeroMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.GroundZeroMultiplier.Value:F0}m effective range");
            DrawSliderFloat("Streets", Plugin.StreetsMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.StreetsMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.StreetsMultiplier.Value:F0}m effective range");
            
            GUILayout.Space(15);
            
            DrawSection("Open Maps");
            DrawSliderFloat("Shoreline", Plugin.ShorelineMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.ShorelineMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.ShorelineMultiplier.Value:F0}m effective range");
            DrawSliderFloat("Lighthouse", Plugin.LighthouseMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.LighthouseMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.LighthouseMultiplier.Value:F0}m effective range");
            DrawSliderFloat("Woods", Plugin.WoodsMultiplier, 0.1f, 3f, "x", $"Base {Plugin.BaseDetectionRange.Value:F0}m * {Plugin.WoodsMultiplier.Value:F1} = {Plugin.BaseDetectionRange.Value * Plugin.WoodsMultiplier.Value:F0}m effective range");
        }

        private void DrawIntegrationsTab()
        {
            DrawSection("SAIN (Solarint's AI Modifications)");

            bool sainLoaded = SAINIntegration.IsSAINLoaded;

            if (!sainLoaded)
            {
                GUI.enabled = false;
                GUILayout.Label("Status: SAIN Mod Not Detected (Integration Disabled)", _tooltipStyle);
                GUILayout.Space(10);
            }
            else
            {
                GUILayout.Label("Status: SAIN Mod Detected & Active", _tooltipStyle);
                GUILayout.Space(10);
            }

            DrawToggle("Enable Integration", Plugin.SAINEnabled, "Allow SAIN personalities to influence vulture behavior.");
            
            GUILayout.Space(10);
            GUILayout.Label("Personality Modifiers", _sectionStyle);
            GUILayout.Label("Aggressive bots (GigaChad, Chad, Wreckless) get a bonus chance to vulture.", _labelStyle);
            DrawSliderInt("Aggression Bonus", Plugin.SAINAggressionModifier, 0, 100, "%", "Bonus chance % for aggressive personalities");
            
            GUILayout.Space(5);
            GUILayout.Label("Cautious bots (Rat, Timmy, Coward) get a reduced chance to vulture.", _labelStyle);
            DrawSliderInt("Cautious Reduction", Plugin.SAINCautiousModifier, 0, 100, "%", "Reduction chance % for cautious personalities");

            // Always re-enable GUI at the end
            GUI.enabled = true;
        }
        
        #endregion
        
        #region UI Helpers
        
        private void DrawSection(string title)
        {
            GUILayout.Space(5);
            GUILayout.Label(title, _sectionStyle);
            GUILayout.Space(3);
        }
        
        private void DrawToggle(string label, ConfigEntry<bool> config, string tooltip)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, tooltip), _labelStyle, GUILayout.Width(180));
            bool newValue = GUILayout.Toggle(config.Value, config.Value ? "ON" : "OFF", _toggleStyle, GUILayout.Width(60));
            if (newValue != config.Value)
            {
                config.Value = newValue;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(5);
        }
        
        private void DrawSliderInt(string label, ConfigEntry<int> config, int min, int max, string suffix, string tooltip)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, tooltip), _labelStyle, GUILayout.Width(180));
            float newValue = GUILayout.HorizontalSlider(config.Value, min, max, GUI.skin.horizontalSlider, _sliderThumbStyle, GUILayout.Width(150));
            GUILayout.Label($"{(int)newValue}{suffix}", _valueStyle, GUILayout.Width(50));
            if ((int)newValue != config.Value)
            {
                config.Value = (int)newValue;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(5);
        }
        
        private void DrawSliderFloat(string label, ConfigEntry<float> config, float min, float max, string suffix, string tooltip)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, tooltip), _labelStyle, GUILayout.Width(180));
            
            float newValue = GUILayout.HorizontalSlider(config.Value, min, max, GUI.skin.horizontalSlider, _sliderThumbStyle, GUILayout.Width(150));
            
            // Round to 2 decimal places for cleaner values
            newValue = (float)Math.Round((double)newValue, 2);
            
            GUILayout.Label($"{newValue:F2}{suffix}", _valueStyle, GUILayout.Width(50));
            
            if (Math.Abs(newValue - config.Value) > 0.001f)
            {
                config.Value = newValue;
            }
            
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(5);
        }
        
        private void ResetAllToDefaults()
        {
            // General
            Plugin.DebugLogging.Value = (bool)Plugin.DebugLogging.DefaultValue;
            Plugin.EnablePMCs.Value = (bool)Plugin.EnablePMCs.DefaultValue;
            Plugin.EnableScavs.Value = (bool)Plugin.EnableScavs.DefaultValue;
            Plugin.EnablePScavs.Value = (bool)Plugin.EnablePScavs.DefaultValue;
            
            // Triggers
            Plugin.VultureChance.Value = (int)Plugin.VultureChance.DefaultValue;
            Plugin.DetectionRange.Value = (float)Plugin.DetectionRange.DefaultValue;
            Plugin.MinSquadSize.Value = (int)Plugin.MinSquadSize.DefaultValue;
            Plugin.EnableExplosionDetection.Value = (bool)Plugin.EnableExplosionDetection.DefaultValue;
            Plugin.IntensityBonus.Value = (int)Plugin.IntensityBonus.DefaultValue;
            Plugin.IntensityWindow.Value = (float)Plugin.IntensityWindow.DefaultValue;
            
            // Behaviors
            Plugin.AmbushDuration.Value = (float)Plugin.AmbushDuration.DefaultValue;
            Plugin.AmbushDistanceMin.Value = (float)Plugin.AmbushDistanceMin.DefaultValue;
            Plugin.AmbushDistanceMax.Value = (float)Plugin.AmbushDistanceMax.DefaultValue;
            Plugin.LootGreed.Value = (bool)Plugin.LootGreed.DefaultValue;
            Plugin.SquadCoordination.Value = (bool)Plugin.SquadCoordination.DefaultValue;
            
            // Advanced
            Plugin.SilentApproach.Value = (bool)Plugin.SilentApproach.DefaultValue;
            Plugin.SilentApproachDistance.Value = (float)Plugin.SilentApproachDistance.DefaultValue;
            Plugin.Paranoia.Value = (bool)Plugin.Paranoia.DefaultValue;
            Plugin.BossAvoidance.Value = (bool)Plugin.BossAvoidance.DefaultValue;
            Plugin.BossAvoidanceRadius.Value = (float)Plugin.BossAvoidanceRadius.DefaultValue;
            Plugin.BossZoneDecay.Value = (float)Plugin.BossZoneDecay.DefaultValue;
            
            // Time
            Plugin.NightTimeModifier.Value = (bool)Plugin.NightTimeModifier.DefaultValue;
            Plugin.NightRangeMultiplier.Value = (float)Plugin.NightRangeMultiplier.DefaultValue;
            
            // Maps
            Plugin.FactoryMultiplier.Value = 0.5f;
            Plugin.LabsMultiplier.Value = 0.67f;
            Plugin.InterchangeMultiplier.Value = 1.0f;
            Plugin.ReserveMultiplier.Value = 1.67f;
            Plugin.CustomsMultiplier.Value = 2.0f;
            Plugin.GroundZeroMultiplier.Value = 1.33f;
            Plugin.StreetsMultiplier.Value = 2.0f;
            Plugin.ShorelineMultiplier.Value = 2.33f;
            Plugin.LighthouseMultiplier.Value = 2.67f;
            Plugin.WoodsMultiplier.Value = 3.0f;
            
            // New Features
            Plugin.EnableVoiceLines.Value = (bool)Plugin.EnableVoiceLines.DefaultValue;
            Plugin.FlashlightDiscipline.Value = (bool)Plugin.FlashlightDiscipline.DefaultValue;
            Plugin.SmartAmbush.Value = (bool)Plugin.SmartAmbush.DefaultValue;
            Plugin.EnableBaiting.Value = (bool)Plugin.EnableBaiting.DefaultValue;
            Plugin.BaitingChance.Value = (int)Plugin.BaitingChance.DefaultValue;

            // Integrations
            Plugin.SAINEnabled.Value = (bool)Plugin.SAINEnabled.DefaultValue;
            Plugin.SAINAggressionModifier.Value = (int)Plugin.SAINAggressionModifier.DefaultValue;
            Plugin.SAINCautiousModifier.Value = (int)Plugin.SAINCautiousModifier.DefaultValue;
            
            Plugin.Log.LogInfo("[Vulture] All settings reset to defaults.");
        }
        
        #endregion
        
        #region Style Initialization
        
        private void InitializeStyles()
        {
            // Create textures
            _bgDarkTex = MakeTex(2, 2, BgDark);
            _bgMidTex = MakeTex(2, 2, BgMid);
            _bgLightTex = MakeTex(2, 2, BgLight);
            _accentTex = MakeTex(2, 2, Accent);
            _highlightTex = MakeTex(2, 2, Highlight);
            _secondaryTex = MakeTex(2, 2, Secondary);
            
            // Window style
            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                normal = { background = _bgDarkTex, textColor = TextLight },
                onNormal = { background = _bgDarkTex, textColor = TextLight },
                border = new RectOffset(10, 10, 10, 10),
                padding = new RectOffset(0, 0, 0, 0)
            };
            
            // Header style
            _headerStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _secondaryTex, textColor = TextLight },
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 10, 0, 0)
            };
            
            // Tab styles
            _tabStyle = new GUIStyle(GUI.skin.button)
            {
                normal = { background = _bgMidTex, textColor = TextDim },
                hover = { background = _bgLightTex, textColor = TextLight },
                active = { background = _accentTex, textColor = TextLight },
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            
            _tabActiveStyle = new GUIStyle(_tabStyle)
            {
                normal = { background = _accentTex, textColor = TextLight },
                hover = { background = _highlightTex, textColor = TextLight }
            };
            
            // Label style
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = TextLight },
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft
            };
            
            // Section header style
            _sectionStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = Highlight },
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            
            // Value label style
            _valueStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = Accent },
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            
            // Toggle style
            _toggleStyle = new GUIStyle(GUI.skin.button)
            {
                normal = { background = _bgLightTex, textColor = TextDim },
                hover = { background = _accentTex, textColor = TextLight },
                active = { background = _highlightTex, textColor = TextLight },
                onNormal = { background = _accentTex, textColor = TextLight },
                onHover = { background = _highlightTex, textColor = TextLight },
                onActive = { background = _highlightTex, textColor = TextLight },
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            
            // Box style for content area
            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _bgMidTex },
                border = new RectOffset(5, 5, 5, 5)
            };
            
            // Close button style
            _closeButtonStyle = new GUIStyle(GUI.skin.button)
            {
                normal = { background = _bgLightTex, textColor = TextLight },
                hover = { background = MakeTex(2, 2, new Color(0.8f, 0.2f, 0.3f)), textColor = TextLight },
                active = { background = MakeTex(2, 2, new Color(1f, 0.3f, 0.4f)), textColor = TextLight },
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            
            // Reset button style
            _resetButtonStyle = new GUIStyle(GUI.skin.button)
            {
                normal = { background = _bgLightTex, textColor = TextLight },
                hover = { background = _accentTex, textColor = TextLight },
                active = { background = _highlightTex, textColor = TextLight },
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            
            // Slider thumb style
            _sliderThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb)
            {
                normal = { background = _accentTex },
                hover = { background = _highlightTex },
                active = { background = _highlightTex },
                fixedWidth = 14,
                fixedHeight = 14
            };
            
            // Tooltip style
            _tooltipStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = TextLight },
                fontSize = 12,
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
        }
        
        private Texture2D MakeTex(int width, int height, Color color)
        {
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }
            Texture2D tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
        
        #endregion
        
        void OnDestroy()
        {
            // Cleanup textures
            if (_bgDarkTex != null) Destroy(_bgDarkTex);
            if (_bgMidTex != null) Destroy(_bgMidTex);
            if (_bgLightTex != null) Destroy(_bgLightTex);
            if (_accentTex != null) Destroy(_accentTex);
            if (_highlightTex != null) Destroy(_highlightTex);
            if (_secondaryTex != null) Destroy(_secondaryTex);
        }
    }
}
