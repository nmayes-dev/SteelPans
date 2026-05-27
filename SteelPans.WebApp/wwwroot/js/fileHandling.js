window.fileHandling = {
    submitDownloadForm: (url, fileName, content, contentType) => {
        const iframe = document.createElement("iframe");
        iframe.name = `download-frame-${Date.now()}`;
        iframe.style.display = "none";

        const form = document.createElement("form");
        form.method = "POST";
        form.action = url;
        form.target = iframe.name;
        form.style.display = "none";

        const addField = (name, value) => {
            const input = document.createElement("input");
            input.type = "hidden";
            input.name = name;
            input.value = value ?? "";
            form.appendChild(input);
        };

        addField("fileName", fileName);
        addField("content", content);
        addField("contentType", contentType);

        document.body.appendChild(iframe);
        document.body.appendChild(form);

        form.submit();
        form.remove();

        setTimeout(() => iframe.remove(), 60_000);
    }
};