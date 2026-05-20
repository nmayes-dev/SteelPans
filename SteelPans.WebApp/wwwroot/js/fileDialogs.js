window.fileDialogs = {
    saveConfigFile: async (fileName, data) => {
        const json = JSON.stringify(data, null, 4);

        const blob = new Blob(
            [json],
            { type: "application/json" });

        const url = URL.createObjectURL(blob);

        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;

        document.body.appendChild(a);
        a.click();
        a.remove();

        URL.revokeObjectURL(url);
    },

    openConfigFile: async () => {
        return new Promise((resolve, reject) => {
            const input = document.createElement("input");

            input.type = "file";
            input.accept = ".pans";

            input.onchange = async (event) => {
                try {
                    const file = event.target.files[0];

                    if (!file) {
                        resolve(null);
                        return;
                    }

                    resolve(JSON.parse(await file.text()));
                }
                catch (error) {
                    reject(error);
                }
            };

            input.oncancel = () => {
                resolve(null);
            };

            input.click();
        });
    },
};