let reference;
function resume() {
    if (!reference || document.visibilityState !== 'visible') return;
    void reference.invokeMethodAsync(navigator.onLine ? 'Resume' : 'Offline');
}
function offline() {
    if (reference) void reference.invokeMethodAsync('Offline');
}
export function watch(dotnet) {
    unwatch();
    reference = dotnet;
    window.addEventListener('online', resume);
    window.addEventListener('offline', offline);
    document.addEventListener('visibilitychange', resume);
    if (!navigator.onLine) offline();
}
export function unwatch() {
    window.removeEventListener('online', resume);
    window.removeEventListener('offline', offline);
    document.removeEventListener('visibilitychange', resume);
    reference = undefined;
}
