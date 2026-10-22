package ru.kodvsego.smartdns;

/** A launcher may advertise pinning and still reject or throw on the request. */
final class WidgetPinning {
    interface Launcher { boolean supported(); boolean request(); }
    enum Result { REQUESTED, MANUAL }
    static Result request(Launcher launcher) {
        try { return launcher.supported()&&launcher.request()?Result.REQUESTED:Result.MANUAL; }
        catch(RuntimeException unavailable) { return Result.MANUAL; }
    }
}
