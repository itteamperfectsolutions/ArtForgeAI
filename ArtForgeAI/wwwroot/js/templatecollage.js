// ===== ArtForge AI - Template Collage File Helpers =====
window.templateCollageHelper = {
    register: function (inputId, dotNetRef, methodName) {
        var el = document.getElementById(inputId);
        if (!el) return;
        el.addEventListener("change", async function () {
            var files = el.files;
            if (!files || files.length === 0) return;
            var results = [];
            for (var i = 0; i < files.length; i++) {
                var file = files[i];
                var base64 = await new Promise(function (resolve) {
                    var reader = new FileReader();
                    reader.onload = function () {
                        resolve(reader.result.split(",")[1]);
                    };
                    reader.readAsDataURL(file);
                });
                results.push({ name: file.name, base64: base64 });
            }
            el.value = "";
            await dotNetRef.invokeMethodAsync(methodName, results);
        });
    },
    click: function (inputId) {
        var el = document.getElementById(inputId);
        if (el) el.click();
    }
};
