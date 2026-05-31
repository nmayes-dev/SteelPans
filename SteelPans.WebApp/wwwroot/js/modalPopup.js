window.modalPopup = {
    submitForm: form => {
        if (!(form instanceof HTMLFormElement)) {
            return;
        }

        form.requestSubmit();
    }
};