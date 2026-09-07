// Keep installed apps current even when Android keeps their windows alive.
if ('serviceWorker' in navigator) {
    let reloading = false;
    navigator.serviceWorker.addEventListener('controllerchange', () => {
        // Also reload on the first successful claim: an older page may have
        // been running without a worker because its installation failed.
        if (!reloading) {
            reloading = true;
            window.location.reload();
        }
    });

    navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' })
        .then(registration => {
            let checking = false;
            async function checkForUpdate() {
                if (checking || document.visibilityState !== 'visible' || !navigator.onLine) return;
                checking = true;
                try {
                    await registration.update();
                } catch {
                    // Offline or incomplete deployments leave the current release usable.
                } finally {
                    checking = false;
                }
            }
            window.addEventListener('online', checkForUpdate);
            window.addEventListener('pageshow', checkForUpdate);
            document.addEventListener('visibilitychange', checkForUpdate);
            window.setInterval(checkForUpdate, 60_000);
            void checkForUpdate();
        })
        .catch(() => {
            // The online app also works when service workers are unavailable.
        });
}
