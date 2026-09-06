using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;

namespace UCP.Bridge
{
    public static class ScreenshotController
    {
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Register(CommandRouter router)
        {
            router.Register("screenshot", HandleScreenshot);
        }

        private static object HandleScreenshot(string paramsJson)
        {
            var p = MiniJson.Deserialize(paramsJson) as Dictionary<string, object>;
            int width = 1920, height = 1080;
            string view = "game";

            if (p != null)
            {
                if (p.TryGetValue("width", out var w)) width = Convert.ToInt32(w);
                if (p.TryGetValue("height", out var h)) height = Convert.ToInt32(h);
                if (p.TryGetValue("view", out var v)) view = v?.ToString() ?? "game";
            }

            // Clamp dimensions for safety
            width = Mathf.Clamp(width, 64, 7680);
            height = Mathf.Clamp(height, 64, 4320);

            byte[] png;

            if (view == "game")
            {
                png = CaptureGameView(width, height);
            }
            else
            {
                // Scene view capture
                png = CaptureSceneView(width, height);
            }

            if (png == null || png.Length == 0)
            {
                throw new Exception("Screenshot capture failed - no camera available");
            }

            string base64 = Convert.ToBase64String(png);

            return new Dictionary<string, object>
            {
                ["width"] = width,
                ["height"] = height,
                ["format"] = "png",
                ["encoding"] = "base64",
                ["data"] = base64,
                ["size"] = png.Length
            };
        }

        private static byte[] CaptureGameView(int width, int height)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                // Try to find any camera
                camera = UnityEngine.Object.FindAnyObjectByType<Camera>();
            }

            if (camera == null)
                throw new Exception("No camera found in scene");

            var rt = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var prevTarget = camera.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                camera.targetTexture = rt;
                camera.Render();
                RenderUiToolkitOverlays(rt, camera.targetDisplay);

                RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture.EncodeToPNG();
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void RenderUiToolkitOverlays(RenderTexture target, int display)
        {
            // Runtime UI Toolkit overlays render after cameras, so Camera.Render() alone misses them.
            var api = ResolveOverlayApi();
            if (!api.IsComplete)
                return;

            var isTransient = typeof(PanelSettings).GetProperty("isTransient", InstanceFlags);
            var redirected = new List<(object panel, PanelSettings settings)>();
            try
            {
                // Redirect active overlays so layout and rendering use the screenshot dimensions.
                foreach (var panel in GetScreenOverlayPanels(api))
                {
                    var settings = panel.GetType()
                        .GetProperty("ownerObject", InstanceFlags)?
                        .GetValue(panel) as PanelSettings;
                    if (settings == null || settings.targetDisplay != display || settings.targetTexture != null ||
                        (bool)(isTransient?.GetValue(settings) ?? false))
                        continue;

                    settings.targetTexture = target;
                    redirected.Add((panel, settings));
                }

                if (redirected.Count == 0)
                    return;

                UpdateAndRepaint(api, redirected);
                api.RenderOffscreenPanels.Invoke(null, null);
            }
            finally
            {
                if (redirected.Count > 0)
                {
                    foreach (var entry in redirected)
                        entry.settings.targetTexture = null;

                    // The overlays are back on the game view viewport, so lay out and repaint again.
                    UpdateAndRepaint(api, redirected);
                }
            }
        }

        private static void UpdateAndRepaint(OverlayApi api, List<(object panel, PanelSettings settings)> redirected)
        {
            // UpdatePanels() lays the panels out for the current viewport but does not repaint them,
            // and RenderOffscreenPanels() only re-submits the draw data a panel already holds. Without
            // an explicit repaint in between, an overlay that was resized or changed renders stale.
            api.UpdatePanels.Invoke(null, null);
            foreach (var entry in redirected)
                api.RepaintPanel.Invoke(null, new[] { entry.panel });
        }

        private static List<object> GetScreenOverlayPanels(OverlayApi api)
        {
            var overlays = new List<object>();
            if (!(api.GetPanels.Invoke(null, null) is IEnumerable panels))
                return overlays;

            foreach (var panel in panels)
            {
                // World-space panels are drawn by the cameras that own them, and RenderOffscreenPanels()
                // asserts against them, so only screen-space overlays may be redirected. Unity 6000.2 and
                // newer filter these out already; earlier Unity 6 versions hand back every player panel.
                if (panel == null)
                    continue;

                var drawsInCameras = panel.GetType().GetProperty("drawsInCameras", InstanceFlags);
                if ((bool)(drawsInCameras?.GetValue(panel) ?? false))
                    continue;

                overlays.Add(panel);
            }

            return overlays;
        }

        // Every member below is internal to UnityEngine.UIElements and has already been renamed once
        // across Unity 6, which silently degrades the capture to camera-only; an editmode test asserts
        // the whole set still resolves on the running editor.
        internal static OverlayApi ResolveOverlayApi()
        {
            var runtime = typeof(VisualElement).Assembly.GetType(
                "UnityEngine.UIElements.UIElementsRuntimeUtility");

            return new OverlayApi
            {
                // Unity 6000.2 and newer expose the screen-overlay subset directly; 6000.0 and 6000.1
                // only expose the full player panel list, which GetScreenOverlayPanels() then filters.
                GetPanels = runtime?.GetMethod("GetSortedScreenOverlayPlayerPanels", StaticFlags)
                    ?? runtime?.GetMethod("GetSortedPlayerPanels", StaticFlags),
                UpdatePanels = runtime?.GetMethod("UpdatePanels", StaticFlags),
                RepaintPanel = runtime?.GetMethod("RepaintPanel", StaticFlags),
                RenderOffscreenPanels = runtime?.GetMethod("RenderOffscreenPanels", StaticFlags)
            };
        }

        internal sealed class OverlayApi
        {
            public MethodInfo GetPanels;
            public MethodInfo UpdatePanels;
            public MethodInfo RepaintPanel;
            public MethodInfo RenderOffscreenPanels;

            public bool IsComplete => GetPanels != null && UpdatePanels != null &&
                RepaintPanel != null && RenderOffscreenPanels != null;
        }

        private static byte[] CaptureSceneView(int width, int height)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
                throw new Exception("No active Scene view");

            var camera = sceneView.camera;
            var rt = new RenderTexture(width, height, 24);
            var prevTarget = camera.targetTexture;

            camera.targetTexture = rt;
            camera.Render();

            RenderTexture.active = rt;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();

            camera.targetTexture = prevTarget;
            RenderTexture.active = null;

            byte[] png = texture.EncodeToPNG();

            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(texture);

            return png;
        }
    }
}
