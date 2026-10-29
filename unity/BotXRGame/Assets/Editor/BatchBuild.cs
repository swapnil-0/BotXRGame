using System;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Headless entry points, for when the Editor is closed:
///
///   Unity.exe -batchmode -quit -projectPath unity/BotXRGame \
///       -executeMethod BatchBuild.SetUpAndBuild -logFile build.log
///
/// Runs the same menu commands a person would (Set Up Cup Detect, Set Up
/// Distance Test, Wire Session Flow - all fill empty fields only), saves the scene, writes the scene
/// report, then builds the APK from the Android XR build profile. In batch
/// mode the tools' dialogs go to the log instead of popping up.
/// </summary>
public static class BatchBuild
{
    private const string ScenePath = "Assets/Scenes/GetStarted_Scene.unity";
    private const string ProfilePath = "Assets/Settings/Build Profiles/Android XR.asset";
    private const string ApkPath = "../../Builds/BotXRGame.apk";

    public static void SetUp()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        // Experience setups first: their summaries ask for Wire Session Flow after.
        SessionFlowSetup.SetUpCupDetect();
        SessionFlowSetup.SetUpDistanceTest();
        SessionFlowSetup.WireSessionFlow();
        SessionFlowSetup.UseHandPinchOnMenu();
        if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene()))
            throw new Exception("Scene save failed: " + ScenePath);
        AssetDatabase.SaveAssets();
        SceneReport.Export();
        Debug.Log("[BatchBuild] Setup done, scene saved, report written");
    }

    public static void Build()
    {
        var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
        if (profile == null) throw new Exception("No build profile at " + ProfilePath);

        // -development on the command line: a debuggable build, so
        // "adb shell run-as com.swapnil.BotXRGame ..." can run tools as the
        // app's own user and SELinux domain (how the NPU path was tested).
        bool dev = System.Array.IndexOf(Environment.GetCommandLineArgs(), "-development") >= 0;

        var report = BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions
        {
            buildProfile = profile,
            locationPathName = ApkPath,
            options = dev ? BuildOptions.Development : BuildOptions.None,
        });
        Debug.Log("[BatchBuild] " + (dev ? "DEVELOPMENT (debuggable) build" : "release build"));
        var s = report.summary;
        Debug.Log($"[BatchBuild] {s.result}: {s.outputPath}, {s.totalSize / (1024 * 1024)} MB, " +
                  $"{s.totalErrors} errors, {s.totalTime}");
        if (s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    public static void SetUpAndBuild()
    {
        SetUp();
        Build();
    }
}
