window.elementContainsActiveElement = element => {
    return element?.contains(document.activeElement) ?? false;
};