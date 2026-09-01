#region License Information (GPL v3)

/*
    XerahS - The Avalonia UI implementation of ShareX
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)
using XerahS.Common;
using XerahS.Platform.Abstractions;
using XerahS.Platform.Linux.Recording;
using XerahS.Platform.Linux.Services;
using XerahS.RegionCapture.ScreenRecording;
namespace XerahS.Platform.Linux
{
    /// <summary>
    /// Which hotkey backend the Linux platform binds, decided from session/portal/desktop facts.
    /// See XIP0077 / XIP0078 / XIP0079.
    /// </summary>
    internal enum LinuxHotkeyBackend
    {
        /// <summary>Direct evdev listener: works on every Wayland compositor and X11 when a keyboard device is readable (XIP0080).</summary>
        Evdev,

        /// <summary>org.freedesktop.portal.GlobalShortcuts is available (works on any compositor that ships it).</summary>
        Portal,

        /// <summary>COSMIC Wayland session without the portal: write a compositor custom shortcut (XIP0079).</summary>
        Cosmic,

        /// <summary>Wayland session without the portal and without a supported writer: the X11 grab cannot deliver, so report unavailable (XIP0078).</summary>
        X11Unavailable,

        /// <summary>Native X11 session: the X11 grab works.</summary>
        X11,
    }

    public static class LinuxPlatform
    {
        public static void Initialize(IScreenCaptureService? screenCaptureService = null, bool useWaylandPortalServices = true)
        {
            var environment = LinuxRuntimeEnvironment.Detect();
            DebugHelper.WriteLine($"Linux: Runtime environment detected: {environment.ToDiagnosticString()}");

            var clipboardService = new LinuxClipboardService();
            IClipboardMonitorService clipboardMonitorService = environment.IsSandboxed
                ? new UnsupportedClipboardMonitorService()
                : new LinuxClipboardMonitorService();

            // Use LinuxScreenCaptureService if none provided
            if (screenCaptureService == null)
            {
                screenCaptureService = new LinuxScreenCaptureService();
                DebugHelper.WriteLine(environment.IsWayland || environment.IsSandboxed
                    ? "Linux: Using LinuxScreenCaptureService with portal-aware capture routing."
                    : "Linux: Using LinuxScreenCaptureService with native X11/CLI fallbacks.");
            }

            bool isWayland = environment.IsWayland;

            // When Wayland portal services are disabled, skip portal-backed hotkeys/input/system
            // and fall back to simpler desktop services.
            // This setting is separate from Linux screenshot/recording preferences.
            // It controls GlobalShortcuts/InputCapture/OpenURI integration only.
            // On native X11, this flag has no effect.
            // On Wayland or sandboxed Linux, disabling it may reduce integration but also avoids portal-specific issues.
            // Keeping this separate avoids overloading the old UseModernCapture toggle.
            //
            // Note: Screen capture and recording continue to make their own backend decisions.
            // EIS connection errors and unnecessary D-Bus connections
            bool usePortalServices = environment.ShouldUsePortalServices(useWaylandPortalServices);
            if (!useWaylandPortalServices && (isWayland || environment.IsSandboxed))
            {
                DebugHelper.WriteLine("Linux: Portal services are disabled. Skipping portal hotkeys/input/system integration. " +
                    "Using fallback services instead. Re-enable and restart to use portal-backed integration.");
            }

            bool hasGlobalShortcuts = usePortalServices && PortalInterfaceChecker.HasInterface("org.freedesktop.portal.GlobalShortcuts");
            bool hasInputCapture = usePortalServices && PortalInterfaceChecker.HasInterface("org.freedesktop.portal.InputCapture");

            IHotkeyService hotkeyService = CreateHotkeyService(environment, hasGlobalShortcuts, isWayland);

            IInputService inputService = hasInputCapture
                ? new WaylandPortalInputService()
                : new LinuxInputService();

            ISystemService systemService = usePortalServices
                ? new WaylandPortalSystemService(allowNativeFallback: !environment.IsSandboxed)
                : new LinuxSystemService();

            IStartupService startupService = CreateStartupService(environment);
            IShellIntegrationService shellIntegrationService = environment.IsSandboxed
                ? new UnsupportedShellIntegrationService()
                : new LinuxShellIntegrationService();
            var notificationService = CreateNotificationService(environment, usePortalServices);

            PlatformServices.Initialize(
                platformInfo: new LinuxPlatformInfo(),
                screenService: new LinuxScreenService(),
                clipboardService: clipboardService,
                windowService: new LinuxWindowService(),
                screenCaptureService: screenCaptureService,
                hotkeyService: hotkeyService,
                inputService: inputService,
                fontService: new LinuxFontService(),
                startupService: startupService,
                systemService: systemService,
                shellIntegrationService: shellIntegrationService,
                notificationService: notificationService,
                diagnosticService: new Services.LinuxDiagnosticService(),
                watchFolderDaemonService: new LinuxWatchFolderDaemonService(),
                clipboardMonitorService: clipboardMonitorService
            );

            // Register OCR service stub (Tesseract integration planned)
            PlatformServices.Ocr = new LinuxOcrService();

            // Initialize theme service for dark mode detection
            PlatformServices.Theme = new LinuxThemeService();
            DebugHelper.WriteLine($"Linux: Theme service initialized. Dark mode preferred: {PlatformServices.Theme.IsDarkModePreferred}");
        }

        /// <summary>
        /// Decides which <see cref="LinuxHotkeyBackend"/> to bind from the session facts. Pure and
        /// deterministic so it can be unit-tested without a live session.
        ///
        /// Preference order:
        /// 1. Direct evdev listener (works on every Wayland compositor and X11) when at least
        ///    one keyboard device is readable. See XIP0080.
        /// 2. XDG GlobalShortcuts portal on sessions that expose it.
        /// 3. COSMIC compositor custom-shortcut writer on portal-less COSMIC Wayland (XIP0079).
        /// 4. Honest GlobalShortcutsUnavailable report on other portal-less Wayland (XIP0078):
        ///    the X11 grab cannot deliver hotkeys to a backgrounded app there.
        /// 5. X11 key grabs (<see cref="LinuxHotkeyService"/>) on native X11.
        /// </summary>
        internal static LinuxHotkeyBackend SelectHotkeyBackend(bool isWayland, bool hasGlobalShortcuts, string? desktop, bool evdevAvailable)
        {
            if (evdevAvailable)
            {
                return LinuxHotkeyBackend.Evdev;
            }

            if (hasGlobalShortcuts)
            {
                return LinuxHotkeyBackend.Portal;
            }

            if (isWayland && string.Equals(desktop, "COSMIC", System.StringComparison.Ordinal))
            {
                return LinuxHotkeyBackend.Cosmic;
            }

            if (isWayland)
            {
                return LinuxHotkeyBackend.X11Unavailable;
            }

            return LinuxHotkeyBackend.X11;
        }

        /// <summary>
        /// Selects the global hotkey provider for the current session using
        /// <see cref="SelectHotkeyBackend"/>. The backend can be forced with the
        /// XERAHS_LINUX_HOTKEY_BACKEND environment variable (values: evdev, portal, cosmic, x11)
        /// for diagnostics and troubleshooting.
        /// </summary>
        private static IHotkeyService CreateHotkeyService(LinuxRuntimeEnvironment environment, bool hasGlobalShortcuts, bool isWayland)
        {
            string? forced = Environment.GetEnvironmentVariable("XERAHS_LINUX_HOTKEY_BACKEND")?.Trim().ToLowerInvariant();

            if (forced == "portal")
            {
                if (hasGlobalShortcuts)
                {
                    DebugHelper.WriteLine("Linux hotkeys: Forced portal backend via XERAHS_LINUX_HOTKEY_BACKEND.");
                    return new WaylandPortalHotkeyService();
                }

                DebugHelper.WriteLine("Linux hotkeys: portal backend forced but GlobalShortcuts portal is unavailable; falling back to X11.");
                return new LinuxHotkeyService();
            }

            if (forced == "x11")
            {
                DebugHelper.WriteLine("Linux hotkeys: Forced X11 backend via XERAHS_LINUX_HOTKEY_BACKEND.");
                return new LinuxHotkeyService();
            }

            if (forced == "cosmic")
            {
                DebugHelper.WriteLine("Linux hotkeys: Forced COSMIC compositor-shortcut backend via XERAHS_LINUX_HOTKEY_BACKEND (XIP0079).");
                return new CosmicHotkeyService();
            }

            bool evdevForced = forced == "evdev";
            bool evdevAvailable = EvdevGlobalHotkeyService.IsAvailable();

            if (evdevForced && !evdevAvailable)
            {
                DebugHelper.WriteLine("Linux hotkeys: evdev backend requested but no readable keyboard devices found. " +
                    "Grant input access (input group / udev rule) or run 'xerahs doctor --linux-input'.");
                // Honor the forced choice even if currently unusable; the service self-reports failures.
                return new EvdevGlobalHotkeyService();
            }

            switch (SelectHotkeyBackend(isWayland, hasGlobalShortcuts, environment.Desktop, evdevAvailable))
            {
                case LinuxHotkeyBackend.Evdev:
                    DebugHelper.WriteLine("Linux hotkeys: Using direct evdev listener (XIP0080).");
                    return new EvdevGlobalHotkeyService();

                case LinuxHotkeyBackend.Portal:
                    DebugHelper.WriteLine("Linux hotkeys: evdev unavailable; using XDG GlobalShortcuts portal.");
                    return new WaylandPortalHotkeyService();

                case LinuxHotkeyBackend.Cosmic:
                    DebugHelper.WriteLine("Linux hotkeys: COSMIC Wayland session without evdev access or " +
                        "org.freedesktop.portal.GlobalShortcuts. Binding global hotkeys through the COSMIC " +
                        "compositor's custom-shortcut config so cosmic-comp spawns XerahS on the keypress (XIP0079).");
                    return new CosmicHotkeyService();

                case LinuxHotkeyBackend.X11Unavailable:
                    DebugHelper.WriteLine("Linux hotkeys: Wayland session without evdev access or " +
                        "org.freedesktop.portal.GlobalShortcuts. Global hotkeys cannot be delivered by the X11 " +
                        "fallback on this compositor; they will be reported as GlobalShortcutsUnavailable. " +
                        "Grant evdev input access ('xerahs doctor --linux-input') or bind a compositor custom " +
                        "shortcut to the XerahS capture verb as a workaround (see XIP0077 / XIP0078 / XIP0080).");
                    return new LinuxHotkeyService(globalShortcutsUnavailable: true);

                default:
                    DebugHelper.WriteLine("Linux hotkeys: Using X11 key grabs.");
                    return new LinuxHotkeyService();
            }
        }

        private static IStartupService CreateStartupService(LinuxRuntimeEnvironment environment)
        {
            if (!environment.IsSandboxed)
            {
                return new LinuxStartupService();
            }

            if (environment.IsFlatpak &&
                PortalInterfaceChecker.HasInterface("org.freedesktop.portal.Background"))
            {
                return new FlatpakPortalStartupService(environment.AppId ?? "io.github.ShareX.XerahS");
            }

            DebugHelper.WriteLine("Linux: Startup integration is unavailable in this sandbox.");
            return new UnsupportedStartupService();
        }

        private static XerahS.Services.Abstractions.INotificationService CreateNotificationService(
            LinuxRuntimeEnvironment environment,
            bool usePortalServices)
        {
            if (usePortalServices &&
                PortalInterfaceChecker.HasInterface("org.freedesktop.portal.Notification"))
            {
                return new PortalNotificationService(allowNativeFallback: !environment.IsSandboxed);
            }

            return new LinuxNotificationService();
        }

        /// <summary>
        /// Initialize screen recording for Linux.
        /// Uses the Wayland ScreenCast portal path when available and falls back to FFmpeg x11grab on X11.
        /// </summary>
        public static void InitializeRecording()
        {
            DebugHelper.WriteLine("LinuxPlatform.InitializeRecording() called");
            try
            {
                var environment = LinuxRuntimeEnvironment.Detect();
                bool isWayland = environment.IsWayland;
                DebugHelper.WriteLine($"LinuxPlatform: isWayland={isWayland}");
                DebugHelper.WriteLine($"LinuxPlatform: recording environment={environment.ToDiagnosticString()}");

                bool hasScreenCastPortal = false;
                if (isWayland || environment.IsSandboxed)
                {
                    hasScreenCastPortal = PortalInterfaceChecker.HasInterface("org.freedesktop.portal.ScreenCast");
                    DebugHelper.WriteLine($"LinuxPlatform: hasScreenCastPortal={hasScreenCastPortal}");
                }

                if (hasScreenCastPortal)
                {
                    DebugHelper.WriteLine("Linux: ScreenCast portal detected. Using Wayland portal recording.");
                    ScreenRecorderService.NativeRecordingServiceFactory = () => new WaylandPortalRecordingService();
                    DebugHelper.WriteLine($"LinuxPlatform: NativeRecordingServiceFactory set = {ScreenRecorderService.NativeRecordingServiceFactory != null}");
                }
                else
                {
                    DebugHelper.WriteLine("Linux: ScreenCast portal NOT detected.");
                    if (isWayland || environment.IsSandboxed)
                    {
                        DebugHelper.WriteLine("WARNING: On Wayland/sandboxed Linux without ScreenCast portal, screen recording may not work properly.");
                        DebugHelper.WriteLine("  - Install xdg-desktop-portal with ScreenCast support for your desktop environment");
                        DebugHelper.WriteLine("  - Ensure PipeWire is running");
                        DebugHelper.WriteLine("  - Common portal backends: xdg-desktop-portal-gnome, xdg-desktop-portal-kde, xdg-desktop-portal-wlr, xdg-desktop-portal-hyprland");
                    }
                    else
                    {
                        DebugHelper.WriteLine("Linux X11: Using FFmpeg x11grab backend.");
                    }
                }

                // FFmpeg fallback only works reliably on X11
                // On Wayland or in Flatpak without portal, this will likely fail
                ScreenRecorderService.FallbackServiceFactory = environment.IsSandboxed
                    ? null
                    : () => new FFmpegRecordingService();

                DebugHelper.WriteLine("Linux: Screen recording initialized successfully");
                DebugHelper.WriteLine(hasScreenCastPortal
                    ? "  - Recording backend: XDG ScreenCast Portal with wf-recorder, FFmpeg(pipewire), or GStreamer(pipewiresrc)"
                    : "  - Recording backend: FFmpeg x11grab fallback");
                DebugHelper.WriteLine("  - Supported modes: Screen, Window, Region");
                DebugHelper.WriteLine("  - Codecs: H.264, HEVC, VP9, AV1 (depends on FFmpeg build)");
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Failed to initialize Linux screen recording");
            }
        }
    }

}
