import SwiftUI
import WebKit
import AVFoundation

@main
struct SampleDrumMobile: App {
    var body: some Scene { WindowGroup { ConnectionView().preferredColorScheme(.dark) } }
}

struct PanelSession: Identifiable {
    let id = UUID()
    let address: PanelAddress
}

struct ConnectionView: View {
    @AppStorage("pcAddress") private var savedPC = ""
    @State private var input = ""
    @State private var error = ""
    @State private var scanning = false
    @State private var session: PanelSession?
    private let accent = Color(red: 0.29, green: 0.78, blue: 0.96)
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                Label("SampleDrum", systemImage: "waveform.circle.fill").font(.largeTitle.bold()).foregroundStyle(accent)
                Text("Seu kit. Na palma da mão.").font(.title.bold())
                Text("Conecte ao PC da igreja para trocar samples e presets sem interromper o áudio.").foregroundStyle(.secondary)
                Button { scanning = true } label: { Label("Ler QR do PC", systemImage: "qrcode.viewfinder").frame(maxWidth: .infinity).padding(8) }
                    .buttonStyle(.borderedProminent).tint(accent).foregroundStyle(.black)
                VStack(alignment: .leading, spacing: 10) {
                    Text("Ou digite o endereço mostrado no PC")
                    TextField("http://192.168.1.10:8750/", text: $input).keyboardType(.URL)
                        .textInputAutocapitalization(.never).autocorrectionDisabled().textFieldStyle(.roundedBorder)
                    if !error.isEmpty { Text(error).foregroundStyle(.red).accessibilityAddTraits(.updatesFrequently) }
                    Button("Conectar ao PC") { connect(input) }.buttonStyle(.bordered).controlSize(.large)
                }
                Text("1. No PC, ative Painel do celular.\n2. Use o mesmo Wi-Fi ou rede local.\n3. Leia o QR ou informe o endereço e digite o código de 8 números.").foregroundStyle(.secondary)
                Text("O áudio continua no PC, mesmo se você sair do app. O celular não precisa de internet para controlar o kit.").font(.footnote).foregroundStyle(.secondary)
                Text("SampleDrum Mobile · 0.4.0").font(.caption).foregroundStyle(.secondary)
            }.padding(26).frame(maxWidth: 600)
        }.background(Color(red: 0.043, green: 0.051, blue: 0.063))
        .onAppear { if input.isEmpty { input = savedPC } }
        .sheet(isPresented: $scanning) {
            QRScanner { result in
                scanning = false
                switch result {
                case .success(let value): connect(value)
                case .failure: error = "Não foi possível usar a câmera. Permita a câmera nos Ajustes ou digite o endereço do PC."
                }
            }.ignoresSafeArea()
                .overlay(alignment: .topTrailing) { Button("Cancelar") { scanning = false }.padding().buttonStyle(.borderedProminent) }
        }
        .fullScreenCover(item: $session) { s in PanelScreen(address: s.address) { session = nil; input = savedPC } }
    }
    private func connect(_ value: String) {
        do {
            let a = try PanelAddress(value)
            savedPC = a.baseURL // Pairing code and raw token never enter UserDefaults.
            error = ""
            session = PanelSession(address: a)
        } catch { self.error = "Use o endereço ou QR mostrado no PC, como http://192.168.1.10:8750/." }
    }
}

struct PanelScreen: View {
    let address: PanelAddress
    let close: () -> Void
    @State private var failure = false
    @State private var reload = 0
    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("SampleDrum").font(.headline).foregroundStyle(.cyan)
                Spacer()
                Button("Trocar PC", action: close)
            }.padding()
            Text(address.origin).font(.caption).foregroundStyle(.secondary).padding(.bottom, 8)
            if failure {
                VStack(spacing: 12) {
                    Text("Sem conexão com o PC").font(.headline)
                    Text("Confira o Wi-Fi, o endereço e se o painel está ligado.")
                    Button("Tentar novamente") { failure = false; reload += 1 }.buttonStyle(.borderedProminent)
                }.padding()
            }
            PanelWebView(address: address, reload: reload, failure: $failure)
        }.background(Color(red: 0.043, green: 0.051, blue: 0.063))
    }
}

struct PanelWebView: UIViewRepresentable {
    let address: PanelAddress
    let reload: Int
    @Binding var failure: Bool
    func makeCoordinator() -> Coordinator { Coordinator(self) }
    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .default() // Persistent HttpOnly pairing cookie, isolated from Safari.
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        let web = WKWebView(frame: .zero, configuration: configuration)
        web.isOpaque = false; web.backgroundColor = .black
        web.navigationDelegate = context.coordinator
        // Block every subresource except the selected PC origin. No native script message handlers are installed.
        let allowed = "^" + NSRegularExpression.escapedPattern(for: address.origin + "/")
        let rules: [[String: Any]] = [
            ["trigger": ["url-filter": ".*"], "action": ["type": "block"]],
            ["trigger": ["url-filter": allowed], "action": ["type": "ignore-previous-rules"]]
        ]
        let json = String(data: try! JSONSerialization.data(withJSONObject: rules), encoding: .utf8)!
        WKContentRuleListStore.default().compileContentRuleList(forIdentifier: "SampleDrumSelectedPC", encodedContentRuleList: json) { list, _ in
            guard let list = list else { self.failure = true; return }
            configuration.userContentController.add(list)
            web.load(URLRequest(url: address.url))
        }
        return web
    }
    func updateUIView(_ web: WKWebView, context: Context) {
        context.coordinator.parent = self
        if context.coordinator.lastReload != reload {
            context.coordinator.lastReload = reload
            web.load(URLRequest(url: address.url))
        }
    }
    static func dismantleUIView(_ web: WKWebView, coordinator: Coordinator) { web.stopLoading(); web.navigationDelegate = nil }
    final class Coordinator: NSObject, WKNavigationDelegate {
        var parent: PanelWebView
        var lastReload = 0
        init(_ parent: PanelWebView) { self.parent = parent }
        func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            guard let url = action.request.url, parent.address.allows(url), action.targetFrame != nil else { decisionHandler(.cancel); return }
            decisionHandler(.allow)
        }
        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
            if (error as NSError).code != NSURLErrorCancelled { parent.failure = true }
        }
        func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { parent.failure = true }
    }
}

struct QRScanner: UIViewControllerRepresentable {
    var result: (Result<String, Error>) -> Void
    func makeUIViewController(context: Context) -> ScannerController { let vc = ScannerController(); vc.result = result; return vc }
    func updateUIViewController(_ vc: ScannerController, context: Context) {}
    static func dismantleUIViewController(_ vc: ScannerController, coordinator: ()) { vc.stop() }
}

final class ScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    var result: ((Result<String, Error>) -> Void)?
    private let capture = AVCaptureSession()
    private let queue = DispatchQueue(label: "SampleDrumQR")
    private var preview: AVCaptureVideoPreviewLayer?
    private var finished = false
    private var stopped = false
    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        AVCaptureDevice.requestAccess(for: .video) { allowed in
            if allowed { self.queue.async { self.configure() } }
            else { DispatchQueue.main.async { self.finish(.failure(NSError(domain: "Camera", code: 1))) } }
        }
    }
    private func configure() {
        guard !stopped else { return }
        do {
            guard let device = AVCaptureDevice.default(for: .video) else { throw NSError(domain: "Camera", code: 2) }
            let input = try AVCaptureDeviceInput(device: device)
            let output = AVCaptureMetadataOutput()
            guard capture.canAddInput(input), capture.canAddOutput(output) else { throw NSError(domain: "Camera", code: 3) }
            capture.addInput(input); capture.addOutput(output)
            output.setMetadataObjectsDelegate(self, queue: .main); output.metadataObjectTypes = [.qr]
            DispatchQueue.main.async {
                let layer = AVCaptureVideoPreviewLayer(session: self.capture); layer.videoGravity = .resizeAspectFill
                self.preview = layer; self.view.layer.addSublayer(layer); layer.frame = self.view.bounds
            }
            capture.startRunning()
        } catch { DispatchQueue.main.async { self.finish(.failure(error)) } }
    }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); preview?.frame = view.bounds }
    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput objects: [AVMetadataObject], from connection: AVCaptureConnection) {
        if let code = objects.first as? AVMetadataMachineReadableCodeObject, let value = code.stringValue { finish(.success(value)) }
    }
    private func finish(_ value: Result<String, Error>) { guard !finished else { return }; finished = true; stop(); result?(value) }
    func stop() { queue.async { self.stopped = true; if self.capture.isRunning { self.capture.stopRunning() } } }
}
