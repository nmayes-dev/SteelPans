window.diagnostics = {
    logInitialConnection: () => {
        const nav = navigator;

        const connection =
            nav.connection ||
            nav.mozConnection ||
            nav.webkitConnection;

        const details = {
            url: window.location.href,
            userAgent: nav.userAgent,
            platform: nav.platform,
            language: nav.language,
            screen: {
                width: window.screen.width,
                height: window.screen.height
            },
            viewport: {
                width: window.innerWidth,
                height: window.innerHeight
            },
            mobile: /Android|iPhone|iPad|iPod|Mobile/i.test(nav.userAgent),
            touch: navigator.maxTouchPoints > 0,
            online: navigator.onLine,
            connection: connection
                ? {
                    effectiveType: connection.effectiveType,
                    type: connection.type,
                    downlink: connection.downlink,
                    rtt: connection.rtt,
                    saveData: connection.saveData
                }
                : null
        };

        return details;
    }
};