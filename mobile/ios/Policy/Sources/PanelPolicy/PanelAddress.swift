import Foundation

public struct PanelAddress: Equatable {
    public let url: URL
    public let origin: String
    public var baseURL: String { origin + "/" }
    public enum InvalidAddress: Error { case invalid }
    public init(_ input: String) throws {
        let trimmed = input.trimmingCharacters(in: .whitespacesAndNewlines)
        let value = trimmed.contains("://") ? trimmed : "http://" + trimmed
        guard let c = URLComponents(string: value), c.scheme?.lowercased() == "http",
              let rawHost = c.host, c.user == nil, c.password == nil, c.query == nil,
              c.path.isEmpty || c.path == "/", c.percentEncodedPath == c.path else { throw InvalidAddress.invalid }
        let host = rawHost.lowercased()
        let port = c.port ?? 8750
        guard (1024...65535).contains(port),
              Self.isPrivateIPv4(host) || host.range(of: "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.local$", options: .regularExpression) != nil,
              c.fragment == nil || (c.percentEncodedFragment == c.fragment && c.fragment!.range(of: "^pair=[0-9]{8}$", options: .regularExpression) != nil)
        else { throw InvalidAddress.invalid }
        origin = "http://\(host):\(port)"
        guard let normalized = URL(string: origin + "/" + (c.fragment.map { "#" + $0 } ?? "")) else { throw InvalidAddress.invalid }
        url = normalized
    }
    public func allows(_ target: URL) -> Bool {
        target.scheme?.lowercased() == "http" && target.host?.lowercased() == url.host &&
            target.port == url.port && target.user == nil && target.password == nil
    }
    private static func isPrivateIPv4(_ value: String) -> Bool {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        var bytes: [Int] = []
        for part in parts {
            guard part.range(of: "^(0|[1-9][0-9]{0,2})$", options: .regularExpression) != nil,
                  let n = Int(part), n <= 255 else { return false }
            bytes.append(n)
        }
        return bytes[0] == 10 || (bytes[0] == 172 && (16...31).contains(bytes[1])) || (bytes[0] == 192 && bytes[1] == 168)
    }
}
