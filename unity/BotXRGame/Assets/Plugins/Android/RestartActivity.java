package com.swapnil.botxr;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;

/**
 * Relaunches the app into its start screen without running its shutdown.
 *
 * Why: an ordinary exit (or a scene reload) stops the AR image-tracking
 * subsystem, and on Galaxy XR that aborts the XR runtime
 * (oxr_xrDestroyTrackableTrackerANDROID, SIGABRT) - repeated aborts have
 * wedged the headset's display until a reboot. So "Back to menu" must not
 * exit normally.
 *
 * How: this activity runs in its own process (":restart", declared in
 * BotXRRestart.androidlib). The app starts it, it SIGKILLs the app's process
 * - no shutdown code runs, so nothing aborts - starts the app again in a
 * fresh task, and exits. The same pattern as ProcessPhoenix. Starting from a
 * visible activity keeps it inside Android's background-launch rules.
 */
public class RestartActivity extends Activity {
    private static final String EXTRA_PID = "botxr.pid";

    /** Called from Unity (ExperienceBar) on the app's main thread. */
    public static void relaunch(Activity from) {
        Intent i = new Intent(from, RestartActivity.class);
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        i.putExtra(EXTRA_PID, android.os.Process.myPid());
        from.startActivity(i);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        int pid = getIntent().getIntExtra(EXTRA_PID, -1);
        if (pid > 0 && pid != android.os.Process.myPid()) {
            android.os.Process.killProcess(pid);
        }

        Intent next = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (next != null) {
            next.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
            startActivity(next);
        }

        finish();
        Runtime.getRuntime().exit(0);
    }
}
