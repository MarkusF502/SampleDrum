package com.markusf502.sampledrum;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.core.app.ActivityScenario;
import org.junit.Test;
import org.junit.runner.RunWith;
import static androidx.test.espresso.Espresso.onView;
import static androidx.test.espresso.action.ViewActions.*;
import static androidx.test.espresso.assertion.ViewAssertions.matches;
import static androidx.test.espresso.matcher.ViewMatchers.*;

@RunWith(AndroidJUnit4.class)
public class LauncherTest {
    @Test public void launcherIsAccessibleAndRejectsAnInternetQrTarget() {
        try (ActivityScenario<MainActivity> activity = ActivityScenario.launch(MainActivity.class)) {
            onView(withText("Ler QR do PC")).check(matches(isDisplayed()));
            onView(withContentDescription("Endereço do PC")).perform(replaceText("http://evil.example:8750/"),closeSoftKeyboard());
            onView(withText("Conectar ao PC")).perform(click());
            onView(withText("Use o endereço ou QR mostrado no PC, como http://192.168.1.10:8750/.")).check(matches(isDisplayed()));
        }
    }
}
