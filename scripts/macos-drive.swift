// External macOS driver for wraithcairn (CI): drives a running app the way a
// person does, from a separate process, through the window server.
//   swift drive.swift click PID          click the middle of PID's largest window
//   swift drive.swift keys SPEC...       e.g. "text:cat > /tmp/x" Return ctrl+shift+i ctrl+d cmd+shift+i
//   swift drive.swift windows PID        list PID's on-screen windows (title, bounds)
import AppKit
import Carbon
import CoreGraphics
import Foundation

let usKeys: [Character: (UInt16, Bool)] = {
    var m: [Character: (UInt16, Bool)] = [:]
    let plain: [(Character, UInt16)] = [
        ("a", 0x00), ("s", 0x01), ("d", 0x02), ("f", 0x03), ("h", 0x04), ("g", 0x05), ("z", 0x06), ("x", 0x07),
        ("c", 0x08), ("v", 0x09), ("b", 0x0B), ("q", 0x0C), ("w", 0x0D), ("e", 0x0E), ("r", 0x0F), ("y", 0x10),
        ("t", 0x11), ("1", 0x12), ("2", 0x13), ("3", 0x14), ("4", 0x15), ("6", 0x16), ("5", 0x17), ("=", 0x18),
        ("9", 0x19), ("7", 0x1A), ("-", 0x1B), ("8", 0x1C), ("0", 0x1D), ("]", 0x1E), ("o", 0x1F), ("u", 0x20),
        ("[", 0x21), ("i", 0x22), ("p", 0x23), ("l", 0x25), ("j", 0x26), ("'", 0x27), ("k", 0x28), (";", 0x29),
        ("\\", 0x2A), (",", 0x2B), ("/", 0x2C), ("n", 0x2D), ("m", 0x2E), (".", 0x2F), (" ", 0x31), ("`", 0x32),
    ]
    for (c, k) in plain { m[c] = (k, false) }
    let shifted: [(Character, Character)] = [
        (">", "."), ("<", ","), ("_", "-"), ("+", "="), (":", ";"), ("\"", "'"), ("|", "\\"), ("?", "/"),
        ("!", "1"), ("@", "2"), ("#", "3"), ("$", "4"), ("%", "5"), ("^", "6"), ("&", "7"), ("*", "8"), ("(", "9"), (")", "0"),
    ]
    for (s, base) in shifted { m[s] = (m[base]!.0, true) }
    for c in "abcdefghijklmnopqrstuvwxyz" { m[Character(c.uppercased())] = (m[c]!.0, true) }
    return m
}()

let named: [String: UInt16] = ["return": 0x24, "tab": 0x30, "escape": 0x35, "space": 0x31]
let src = CGEventSource(stateID: .hidSystemState)

func post(_ code: UInt16, _ down: Bool, _ flags: CGEventFlags) {
    let e = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: down)!
    e.flags = flags
    e.post(tap: .cghidEventTap)
    usleep(15_000)
}

func chord(_ code: UInt16, _ mods: [String]) {
    var flags: CGEventFlags = []
    let modKeys: [(String, UInt16, CGEventFlags)] = [
        ("ctrl", 0x3B, .maskControl), ("shift", 0x38, .maskShift), ("alt", 0x3A, .maskAlternate), ("cmd", 0x37, .maskCommand),
    ]
    var pressed: [(UInt16, CGEventFlags)] = []
    for (name, key, flag) in modKeys where mods.contains(name) {
        flags.insert(flag)
        post(key, true, flags)
        pressed.append((key, flag))
    }
    post(code, true, flags)
    post(code, false, flags)
    for (key, flag) in pressed.reversed() {
        flags.remove(flag)
        post(key, false, flags)
    }
    usleep(40_000)
}

func windows(_ pid: Int32) -> [[String: Any]] {
    let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] ?? []
    return list.filter { ($0[kCGWindowOwnerPID as String] as? Int32) == pid }
}

let args = Array(CommandLine.arguments.dropFirst())
switch args.first {
case "layout":
    // Select (enabling first) a keyboard layout, e.g. com.apple.keylayout.Dvorak.
    let id = args[1]
    let filter = [kTISPropertyInputSourceID as String: id] as CFDictionary
    guard let list = TISCreateInputSourceList(filter, true)?.takeRetainedValue() as? [TISInputSource], let s = list.first else {
        print("DRIVE no input source \(id)"); exit(1)
    }
    TISEnableInputSource(s)
    let rc = TISSelectInputSource(s)
    usleep(500_000)
    let cur = TISCopyCurrentKeyboardInputSource().takeRetainedValue()
    let curId = Unmanaged<CFString>.fromOpaque(TISGetInputSourceProperty(cur, kTISPropertyInputSourceID)).takeUnretainedValue()
    print("DRIVE layout select rc=\(rc) current=\(curId)")
case "keycode":
    // keycode CODE MODS...: raw virtual key code (physical position), e.g. 0x05 ctrl shift
    chord(UInt16(args[1].dropFirst(2), radix: 16)!, Array(args.dropFirst(2)))
case "windows":
    let pid = Int32(args[1])!
    for w in windows(pid) {
        let b = w[kCGWindowBounds as String] as? [String: Any] ?? [:]
        print("WINDOW name=\(w[kCGWindowName as String] ?? "-") layer=\(w[kCGWindowLayer as String] ?? "-") bounds=\(b)")
    }
case "click":
    let pid = Int32(args[1])!
    NSRunningApplication(processIdentifier: pid)?.activate(options: [.activateIgnoringOtherApps])
    usleep(500_000)
    let ws = windows(pid).compactMap { w -> CGRect? in
        guard let b = w[kCGWindowBounds as String] as? NSDictionary else { return nil }
        return CGRect(dictionaryRepresentation: b)
    }.sorted { $0.width * $0.height > $1.width * $1.height }
    guard let r = ws.first else { print("DRIVE no window for \(pid)"); exit(1) }
    let p = CGPoint(x: r.midX, y: r.midY + 40)
    for t in [CGEventType.leftMouseDown, .leftMouseUp] {
        CGEvent(mouseEventSource: src, mouseType: t, mouseCursorPosition: p, mouseButton: .left)!.post(tap: .cghidEventTap)
        usleep(60_000)
    }
    print("DRIVE clicked \(p) in \(r) front=\(NSWorkspace.shared.frontmostApplication?.processIdentifier ?? -1)")
case "keys":
    for spec in args.dropFirst() {
        if spec.hasPrefix("text:") {
            for c in spec.dropFirst(5) {
                guard let (k, shift) = usKeys[c] else { print("DRIVE no key for \(c)"); continue }
                chord(k, shift ? ["shift"] : [])
            }
            continue
        }
        if spec.hasPrefix("sleep:") { usleep(UInt32(Double(spec.dropFirst(6))! * 1_000_000)); continue }
        var parts = spec.lowercased().split(separator: "+").map(String.init)
        let key = parts.removeLast()
        let code = named[key] ?? usKeys[Character(key)]?.0
        guard let code else { print("DRIVE unknown key \(spec)"); continue }
        chord(code, parts)
    }
default:
    print("usage: drive.swift click PID | keys SPEC... | windows PID")
}
