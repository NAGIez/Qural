import AppKit
import Carbon

// Глобальные горячие клавиши через Carbon (RegisterEventHotKey): работают в любой программе
// и не требуют разрешения «Универсальный доступ».
@MainActor
final class HotKeys {
    static let shared = HotKeys()

    /// ⌃⌥ — Control + Option.
    static let ctrlOpt = UInt32(controlKey | optionKey)

    private var handlers: [UInt32: () -> Void] = [:]
    private var refs: [EventHotKeyRef] = []
    private var installed = false

    /// false — сочетание уже занято (другой программой или системой).
    @discardableResult
    func register(id: UInt32, keyCode: Int, modifiers: UInt32, _ action: @escaping () -> Void) -> Bool {
        install()
        handlers[id] = action
        var ref: EventHotKeyRef?
        let hk = EventHotKeyID(signature: OSType(0x5152_4C31) /* 'QRL1' */, id: id)
        let err = RegisterEventHotKey(UInt32(keyCode), modifiers, hk, GetApplicationEventTarget(), 0, &ref)
        if err == noErr, let ref { refs.append(ref); return true }
        Log.write("горячая клавиша \(id): не удалось занять (\(err))")
        return false
    }

    fileprivate func fire(_ id: UInt32) { handlers[id]?() }

    private func install() {
        guard !installed else { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), hotKeyPressed, 1, &spec, nil, nil)
    }
}

// обработчик Carbon — обычная функция (указатель на C-функцию), без захвата контекста
private func hotKeyPressed(_ next: EventHandlerCallRef?, _ event: EventRef?, _ data: UnsafeMutableRawPointer?) -> OSStatus {
    var hk = EventHotKeyID()
    GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                      nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
    let id = hk.id
    DispatchQueue.main.async { MainActor.assumeIsolated { HotKeys.shared.fire(id) } }
    return noErr
}
