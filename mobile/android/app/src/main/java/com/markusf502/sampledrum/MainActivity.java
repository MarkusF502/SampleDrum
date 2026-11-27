package com.markusf502.sampledrum;

import android.annotation.SuppressLint;
import android.graphics.Color;
import android.os.Bundle;
import android.text.InputType;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.InputMethodManager;
import android.webkit.*;
import android.widget.*;
import androidx.activity.ComponentActivity;
import androidx.activity.OnBackPressedCallback;
import androidx.activity.result.ActivityResultLauncher;
import com.journeyapps.barcodescanner.ScanContract;
import com.journeyapps.barcodescanner.ScanOptions;
import java.io.ByteArrayInputStream;

/** Native launcher and QR reader; the PC page has no native JavaScript bridge or device permissions. */
public final class MainActivity extends ComponentActivity {
    private final int bg = Color.rgb(11,13,16), ink = Color.rgb(240,243,248), muted = Color.rgb(160,171,185), accent = Color.rgb(73,200,245);
    private LinearLayout root;
    private EditText address;
    private TextView error;
    private WebView panel;
    private PanelAddress selected;
    private final ActivityResultLauncher<ScanOptions> scanner = registerForActivityResult(new ScanContract(), result -> {
        if (result.getContents() != null) connect(result.getContents());
    });

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getOnBackPressedDispatcher().addCallback(this, new OnBackPressedCallback(true) {
            @Override public void handleOnBackPressed() { if (panel != null) showHome(); else finish(); }
        });
        showHome();
        if (state != null && state.getString("origin") != null) connect(state.getString("origin"));
    }

    private int dp(int v) { return Math.round(v * getResources().getDisplayMetrics().density); }
    private TextView text(String value, int size, int color) {
        TextView t = new TextView(this); t.setText(value); t.setTextSize(size); t.setTextColor(color);
        t.setPadding(0, dp(8), 0, dp(8)); return t;
    }
    private Button button(String title, View.OnClickListener action) {
        Button b = new Button(this); b.setText(title); b.setAllCaps(false); b.setTextColor(ink); b.setMinHeight(dp(52));
        b.setOnClickListener(action); return b;
    }
    private void createRoot() {
        root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL); root.setBackgroundColor(bg);
        root.setOnApplyWindowInsetsListener((v, insets) -> {
            if (android.os.Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(android.view.WindowInsets.Type.systemBars() | android.view.WindowInsets.Type.ime());
                v.setPadding(bars.left, bars.top, bars.right, bars.bottom);
            } else {
                v.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
            }
            return insets;
        });
        setContentView(root);
    }
    private void destroyPanel() {
        if (panel != null) {
            CookieManager.getInstance().flush();
            panel.stopLoading(); panel.loadUrl("about:blank"); panel.destroy(); panel = null;
        }
        selected = null;
    }
    private void showHome() {
        destroyPanel(); createRoot();
        ScrollView scroll = new ScrollView(this);
        LinearLayout content = new LinearLayout(this); content.setOrientation(LinearLayout.VERTICAL); content.setPadding(dp(24),dp(28),dp(24),dp(24));
        scroll.addView(content); root.addView(scroll);
        content.addView(text("◉  SampleDrum", 30, accent));
        content.addView(text("Seu kit. Na palma da mão.", 24, ink));
        content.addView(text("Conecte ao PC da igreja para trocar samples e presets sem interromper o áudio.", 17, muted));
        Button qr = button("Ler QR do PC", v -> scanner.launch(new ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
            .setPrompt("Leia o QR em Painel do celular no PC").setBeepEnabled(false).setOrientationLocked(false)));
        content.addView(qr);
        content.addView(text("Ou digite o endereço mostrado no PC", 16, ink));
        address = new EditText(this); address.setSingleLine(true); address.setTextColor(ink); address.setTextSize(17);
        address.setHintTextColor(muted); address.setHint("http://192.168.1.10:8750/");
        address.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        address.setText(getPreferences(MODE_PRIVATE).getString("pc", ""));
        content.addView(address, new LinearLayout.LayoutParams(-1, dp(56)));
        error = text("", 15, Color.rgb(255,169,157)); error.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); content.addView(error);
        content.addView(button("Conectar ao PC", v -> connect(address.getText().toString())));
        content.addView(text("1. No PC, ative Painel do celular.\n2. Use o mesmo Wi-Fi ou rede local.\n3. Leia o QR ou informe o endereço e digite o código de 8 números.", 16, muted));
        content.addView(text("O áudio continua no PC, mesmo se você sair do app. O celular não precisa de internet para controlar o kit.", 14, muted));
        content.addView(text("SampleDrum Mobile · 0.4.0", 13, muted));
    }

    @SuppressLint("SetJavaScriptEnabled")
    private void connect(String input) {
        PanelAddress next;
        try { next = PanelAddress.parse(input); }
        catch (IllegalArgumentException e) { if (error != null) error.setText(e.getMessage()); return; }
        ((InputMethodManager)getSystemService(INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(root.getWindowToken(), 0);
        destroyPanel(); selected = next;
        // Persist the address only. Never save a QR code, pairing code or raw device token in app preferences.
        getPreferences(MODE_PRIVATE).edit().putString("pc", next.origin + "/").apply();
        createRoot();
        LinearLayout toolbar = new LinearLayout(this); toolbar.setPadding(dp(8),0,dp(8),0);
        TextView title = text("SampleDrum",18,accent); toolbar.addView(title,new LinearLayout.LayoutParams(0,-2,1));
        toolbar.addView(button("Trocar PC",v -> showHome())); root.addView(toolbar);
        TextView status = text("Conectando ao PC…",14,muted); status.setPadding(dp(16),dp(6),dp(16),dp(6)); root.addView(status);
        Button retry = button("Tentar novamente",v -> panel.reload()); retry.setVisibility(View.GONE); root.addView(retry);
        panel = new WebView(this); panel.setBackgroundColor(bg);
        WebSettings settings = panel.getSettings();
        settings.setJavaScriptEnabled(true); settings.setDomStorageEnabled(true);
        settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
        settings.setGeolocationEnabled(false); settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setSupportMultipleWindows(false); settings.setSafeBrowsingEnabled(true);
        CookieManager.getInstance().setAcceptCookie(true);
        CookieManager.getInstance().setAcceptThirdPartyCookies(panel,false);
        panel.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                return !next.allows(request.getUrl().toString());
            }
            @Override public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
                if (next.allows(request.getUrl().toString())) return null;
                return new WebResourceResponse("text/plain","UTF-8",403,"Blocked",null,new ByteArrayInputStream(new byte[0]));
            }
            @Override public void onPageFinished(WebView view,String url) {
                CookieManager.getInstance().flush();
                if (retry.getVisibility() != View.VISIBLE) { status.setText(next.origin); }
            }
            @Override public void onReceivedError(WebView view,WebResourceRequest request,WebResourceError failure) {
                if (request.isForMainFrame()) {
                    status.setText("Sem conexão com o PC. Confira o Wi-Fi, o endereço e se o painel está ligado.");
                    retry.setVisibility(View.VISIBLE);
                }
            }
            @Override public void onPageStarted(WebView view,String url,android.graphics.Bitmap icon) {
                retry.setVisibility(View.GONE); status.setText("Conectando ao PC…");
            }
        });
        // No WebChromeClient permission handler, file chooser, downloads, popups or addJavascriptInterface.
        root.addView(panel,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,0,1));
        panel.loadUrl(next.url.toString());
    }
    @Override protected void onSaveInstanceState(Bundle state) {
        if (selected != null) state.putString("origin",selected.origin + "/");
        super.onSaveInstanceState(state);
    }
    @Override protected void onDestroy() { destroyPanel(); super.onDestroy(); }
}
