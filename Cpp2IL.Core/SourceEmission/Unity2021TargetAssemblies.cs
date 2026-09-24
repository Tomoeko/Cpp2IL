using System;
using System.Collections.Generic;
using AsmResolver.DotNet;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Unity managed assembly identities supplied by the Unity 2021.3.35f1 Windows editor.
/// Package assemblies are deliberately excluded from this target reference catalog.
/// </summary>
internal static class Unity2021TargetAssemblies
{
    private static readonly Version ModuleVersion = new(0, 0, 0, 0);
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "UnityEditor",
        "UnityEditor.CoreModule",
        "UnityEditor.DeviceSimulatorModule",
        "UnityEditor.DiagnosticsModule",
        "UnityEditor.GraphViewModule",
        "UnityEditor.PackageManagerUIModule",
        "UnityEditor.QuickSearchModule",
        "UnityEditor.SceneTemplateModule",
        "UnityEditor.TextCoreFontEngineModule",
        "UnityEditor.TextCoreTextEngineModule",
        "UnityEditor.UIBuilderModule",
        "UnityEditor.UIElementsModule",
        "UnityEditor.UIElementsSamplesModule",
        "UnityEditor.UIServiceModule",
        "UnityEditor.UnityConnectModule",
        "UnityEngine",
        "UnityEngine.AIModule",
        "UnityEngine.ARModule",
        "UnityEngine.AccessibilityModule",
        "UnityEngine.AndroidJNIModule",
        "UnityEngine.AnimationModule",
        "UnityEngine.AssetBundleModule",
        "UnityEngine.AudioModule",
        "UnityEngine.ClothModule",
        "UnityEngine.ClusterInputModule",
        "UnityEngine.ClusterRendererModule",
        "UnityEngine.CoreModule",
        "UnityEngine.CrashReportingModule",
        "UnityEngine.DSPGraphModule",
        "UnityEngine.DirectorModule",
        "UnityEngine.GIModule",
        "UnityEngine.GameCenterModule",
        "UnityEngine.GridModule",
        "UnityEngine.HotReloadModule",
        "UnityEngine.IMGUIModule",
        "UnityEngine.ImageConversionModule",
        "UnityEngine.InputLegacyModule",
        "UnityEngine.InputModule",
        "UnityEngine.JSONSerializeModule",
        "UnityEngine.LocalizationModule",
        "UnityEngine.NVIDIAModule",
        "UnityEngine.ParticleSystemModule",
        "UnityEngine.PerformanceReportingModule",
        "UnityEngine.Physics2DModule",
        "UnityEngine.PhysicsModule",
        "UnityEngine.ProfilerModule",
        "UnityEngine.RuntimeInitializeOnLoadManagerInitializerModule",
        "UnityEngine.ScreenCaptureModule",
        "UnityEngine.SharedInternalsModule",
        "UnityEngine.SpriteMaskModule",
        "UnityEngine.SpriteShapeModule",
        "UnityEngine.StreamingModule",
        "UnityEngine.SubstanceModule",
        "UnityEngine.SubsystemsModule",
        "UnityEngine.TLSModule",
        "UnityEngine.TerrainModule",
        "UnityEngine.TerrainPhysicsModule",
        "UnityEngine.TextCoreFontEngineModule",
        "UnityEngine.TextCoreTextEngineModule",
        "UnityEngine.TextRenderingModule",
        "UnityEngine.TilemapModule",
        "UnityEngine.UIElementsModule",
        "UnityEngine.UIElementsNativeModule",
        "UnityEngine.UIModule",
        "UnityEngine.UNETModule",
        "UnityEngine.UmbraModule",
        "UnityEngine.UnityAnalyticsCommonModule",
        "UnityEngine.UnityAnalyticsModule",
        "UnityEngine.UnityConnectModule",
        "UnityEngine.UnityCurlModule",
        "UnityEngine.UnityTestProtocolModule",
        "UnityEngine.UnityWebRequestAssetBundleModule",
        "UnityEngine.UnityWebRequestAudioModule",
        "UnityEngine.UnityWebRequestModule",
        "UnityEngine.UnityWebRequestTextureModule",
        "UnityEngine.UnityWebRequestWWWModule",
        "UnityEngine.VFXModule",
        "UnityEngine.VRModule",
        "UnityEngine.VehiclesModule",
        "UnityEngine.VideoModule",
        "UnityEngine.VirtualTexturingModule",
        "UnityEngine.WindModule",
        "UnityEngine.XRModule",
    };

    internal static bool IsKnownName(string name) => Names.Contains(name);

    internal static bool HasTargetIdentity(AssemblyReference reference) =>
        reference.Name?.ToString() is { } name && Names.Contains(name) &&
        reference.Version == ModuleVersion &&
        string.IsNullOrEmpty(reference.Culture?.ToString()) &&
        (reference.PublicKeyOrToken?.Length ?? 0) == 0;
}
