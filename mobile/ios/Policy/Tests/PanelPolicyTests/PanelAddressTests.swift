import XCTest
@testable import PanelPolicy

final class PanelAddressTests: XCTestCase {
    func testPrivatePCAndQRCode() throws {
        for host in ["192.168.1.10", "10.0.2.2", "172.16.1.1", "172.31.255.254", "pc-igreja.local"] {
            let a = try PanelAddress("http://\(host):8750/#pair=12345678")
            XCTAssertEqual(a.origin, "http://\(host):8750")
            XCTAssertFalse(a.baseURL.contains("pair"))
            XCTAssertTrue(a.allows(URL(string: a.origin + "/api/events")!))
            XCTAssertFalse(a.allows(URL(string: "http://evil.example:8750/")!))
            XCTAssertFalse(a.allows(URL(string: "http://\(host):8751/")!))
        }
        XCTAssertEqual(try PanelAddress("192.168.1.10").baseURL,"http://192.168.1.10:8750/")
    }
    func testRejectsUntrustedTargets() {
        for value in ["", "https://192.168.1.10:8750/", "http://8.8.8.8:8750/", "http://127.0.0.1:8750/",
            "http://localhost:8750/", "http://169.254.1.1:8750/", "http://172.32.1.1:8750/", "http://192.168.01.1:8750/",
            "http://192.168.1.256:8750/", "http://user@192.168.1.10:8750/", "file:///etc/passwd", "javascript:alert(1)",
            "http://192.168.1.10:8750/evil", "http://192.168.1.10:8750/?x=1", "http://192.168.1.10:8750/#pair=123456",
            "http://192.168.1.10:80/", "http://pc.local.evil.example:8750/", "http://192.168.1.10:8750/#pair=12345678&x=1"] {
            XCTAssertThrowsError(try PanelAddress(value), value)
        }
    }
}
