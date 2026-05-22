window.toolbar = {
    waitForSurfaceTransition: (element, dotNetRef) => {
        const surface = element.querySelector(".toolbar__surface");

        if (!surface) {
            dotNetRef.invokeMethodAsync("OnToolbarTransitionEnded");
            return;
        }

        const handler = event => {
            if (event.target !== surface) {
                return;
            }

            if (
                event.propertyName !== "max-width" &&
                event.propertyName !== "max-height"
            ) {
                return;
            }

            surface.removeEventListener("transitionend", handler);

            dotNetRef.invokeMethodAsync("OnToolbarTransitionEnded");
        };

        surface.addEventListener("transitionend", handler);
    }
};