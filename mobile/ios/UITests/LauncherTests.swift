import XCTest
final class LauncherTests: XCTestCase {
    func testLauncherAndRejectInternetAddress() {
        let app = XCUIApplication()
        app.launch()
        XCTAssertTrue(app.buttons["Ler QR do PC"].waitForExistence(timeout: 10))
        let input = app.textFields["pcAddress"]
        input.tap()
        input.typeText("http://evil.example:8750/")
        app.buttons["Conectar ao PC"].tap()
        XCTAssertTrue(app.staticTexts["Use o endereço ou QR mostrado no PC, como http://192.168.1.10:8750/."].waitForExistence(timeout: 5))
        let shot = XCTAttachment(screenshot: app.screenshot())
        shot.lifetime = .keepAlways
        add(shot)
    }
}
