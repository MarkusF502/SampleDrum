// swift-tools-version: 5.9
import PackageDescription
let package = Package(name: "PanelPolicy", products: [.library(name: "PanelPolicy", targets: ["PanelPolicy"])],
    targets: [.target(name: "PanelPolicy"), .testTarget(name: "PanelPolicyTests", dependencies: ["PanelPolicy"])])
