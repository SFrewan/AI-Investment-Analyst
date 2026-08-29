// The two things a Blazor component cannot do to the document it is hosted in, and nothing else.
//
// The language preference is kept in sessionStorage rather than localStorage: it is a property of
// this sitting, and it is cleared with everything else when the operator signs out. Every accessor
// is guarded, because a browser configured to block site data throws rather than returning null.
window.dashboardShell = {
    setDocumentLanguage: function (code, direction) {
        document.documentElement.setAttribute('lang', code);
        document.documentElement.setAttribute('dir', direction);
    },

    readLanguage: function () {
        try {
            return window.sessionStorage.getItem('dashboard.language');
        } catch {
            return null;
        }
    },

    writeLanguage: function (code) {
        try {
            window.sessionStorage.setItem('dashboard.language', code);
        } catch {
            // A browser that refuses site data still gets a working dashboard; it just does not
            // remember the language across a reload.
        }
    },

    clearSession: function () {
        try {
            window.sessionStorage.clear();
        } catch {
            // Nothing to clear, or nothing clearable. Sign-out has already dropped the key from
            // memory, which is where it actually lived.
        }
    },

    browserLanguage: function () {
        return navigator.language || 'en';
    }
};
