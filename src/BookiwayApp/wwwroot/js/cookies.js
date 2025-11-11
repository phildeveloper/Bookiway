// Simple cookie helpers
window.cookies = {
  set: function (name, value, days) {
    try {
      const d = new Date();
      const maxDays = typeof days === "number" ? days : 365;
      d.setTime(d.getTime() + maxDays * 24 * 60 * 60 * 1000);
      const expires = "expires=" + d.toUTCString();
      document.cookie = name + "=" + encodeURIComponent(value || "") + ";" + expires + ";path=/;SameSite=Lax";
      return true;
    } catch {
      return false;
    }
  },
  get: function (name) {
    try {
      const cname = name + "=";
      const decodedCookie = decodeURIComponent(document.cookie);
      const ca = decodedCookie.split(";");
      for (let i = 0; i < ca.length; i++) {
        let c = ca[i];
        while (c.charAt(0) === " ") c = c.substring(1);
        if (c.indexOf(cname) === 0) {
          return c.substring(cname.length, c.length);
        }
      }
      return null;
    } catch {
      return null;
    }
  }
};


