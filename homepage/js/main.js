/* PeerSync site scripts. No dependencies, no network calls. */
(function () {
  "use strict";

  var doc = document;

  /* Mobile navigation */
  var toggle = doc.querySelector(".nav-toggle");
  var links = doc.querySelector(".nav-links");
  if (toggle && links) {
    toggle.addEventListener("click", function () {
      var open = links.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
    links.addEventListener("click", function (e) {
      if (e.target.tagName === "A") {
        links.classList.remove("open");
        toggle.setAttribute("aria-expanded", "false");
      }
    });
  }

  /* Mark the current page in the navigation */
  var here = location.pathname.split("/").pop() || "index.html";
  doc.querySelectorAll(".nav-links a").forEach(function (a) {
    var target = (a.getAttribute("href") || "").split("#")[0];
    if (target && target === here) a.setAttribute("aria-current", "page");
  });

  /* Scroll reveal */
  var revealed = doc.querySelectorAll(".reveal");
  if ("IntersectionObserver" in window && revealed.length) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          entry.target.classList.add("in");
          io.unobserve(entry.target);
        }
      });
    }, { rootMargin: "0px 0px -8% 0px", threshold: 0.08 });
    revealed.forEach(function (el) { io.observe(el); });
  } else {
    revealed.forEach(function (el) { el.classList.add("in"); });
  }

  /* Card spotlight follows the pointer */
  doc.querySelectorAll(".card").forEach(function (card) {
    card.addEventListener("pointermove", function (e) {
      var r = card.getBoundingClientRect();
      card.style.setProperty("--mx", ((e.clientX - r.left) / r.width * 100).toFixed(1) + "%");
      card.style.setProperty("--my", ((e.clientY - r.top) / r.height * 100).toFixed(1) + "%");
    });
  });

  /* Copy buttons */
  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text);
    }
    return new Promise(function (resolve, reject) {
      var ta = doc.createElement("textarea");
      ta.value = text;
      ta.setAttribute("readonly", "");
      ta.style.position = "fixed";
      ta.style.opacity = "0";
      doc.body.appendChild(ta);
      ta.select();
      try { doc.execCommand("copy"); resolve(); } catch (err) { reject(err); }
      doc.body.removeChild(ta);
    });
  }
  doc.querySelectorAll("[data-copy]").forEach(function (btn) {
    var original = btn.textContent;
    btn.addEventListener("click", function () {
      var text = btn.getAttribute("data-copy");
      copyText(text).then(function () {
        btn.textContent = "Copied";
        btn.classList.add("done");
        setTimeout(function () { btn.textContent = original; btn.classList.remove("done"); }, 1600);
      }).catch(function () {
        btn.textContent = "Select it";
        setTimeout(function () { btn.textContent = original; }, 1600);
      });
    });
  });

  /* Step tabs (How it works) */
  var tabs = doc.querySelectorAll("[role=tab]");
  if (tabs.length) {
    function activate(tab) {
      tabs.forEach(function (t) {
        var on = t === tab;
        t.setAttribute("aria-selected", on ? "true" : "false");
        t.setAttribute("tabindex", on ? "0" : "-1");
        var panel = doc.getElementById(t.getAttribute("aria-controls"));
        if (panel) panel.hidden = !on;
      });
    }
    tabs.forEach(function (tab, i) {
      tab.addEventListener("click", function () { activate(tab); });
      tab.addEventListener("keydown", function (e) {
        var next = null;
        if (e.key === "ArrowDown" || e.key === "ArrowRight") next = tabs[(i + 1) % tabs.length];
        if (e.key === "ArrowUp" || e.key === "ArrowLeft") next = tabs[(i - 1 + tabs.length) % tabs.length];
        if (next) { e.preventDefault(); activate(next); next.focus(); }
      });
    });
    /* Gentle auto-advance until the visitor interacts */
    var idx = 0, auto = setInterval(function () {
      idx = (idx + 1) % tabs.length;
      activate(tabs[idx]);
    }, 4200);
    tabs.forEach(function (t) { t.addEventListener("click", function () { clearInterval(auto); }); });
  }

  /* Footer year */
  doc.querySelectorAll("[data-year]").forEach(function (el) { el.textContent = String(new Date().getFullYear()); });

  /* Static contact form: validates locally, then hands the message to the visitor's mail client. */
  var form = doc.getElementById("contact-form");
  if (form) {
    var result = doc.getElementById("form-result");
    var mailto = form.getAttribute("data-mailto") || "";

    function setInvalid(field, bad) {
      var wrap = field.closest(".field");
      if (wrap) wrap.classList.toggle("invalid", bad);
      field.setAttribute("aria-invalid", bad ? "true" : "false");
    }
    function validEmail(v) { return /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(v); }

    form.querySelectorAll(".input").forEach(function (f) {
      f.addEventListener("input", function () { setInvalid(f, false); });
    });

    form.addEventListener("submit", function (e) {
      e.preventDefault();
      var name = form.elements.name;
      var email = form.elements.email;
      var topic = form.elements.topic;
      var message = form.elements.message;
      var consent = form.elements.consent;
      var honeypot = form.elements.website;
      var ok = true;

      var checks = [
        [name, !!name.value.trim()],
        [email, validEmail(email.value.trim())],
        [topic, !!topic.value],
        [message, message.value.trim().length >= 10]
      ];
      checks.forEach(function (c) {
        setInvalid(c[0], !c[1]);
        if (!c[1]) ok = false;
      });
      if (!consent.checked) {
        consent.closest(".field").classList.add("invalid");
        ok = false;
      } else {
        consent.closest(".field").classList.remove("invalid");
      }
      if (honeypot && honeypot.value) ok = false;

      if (!ok) {
        result.className = "form-result show error";
        result.textContent = "Please check the highlighted fields.";
        var first = form.querySelector(".invalid .input");
        if (first) first.focus();
        return;
      }

      var org = form.elements.organization.value.trim();
      var subject = "[PeerSync] " + topic.options[topic.selectedIndex].text + (org ? " - " + org : "");
      var body = [
        "Name: " + name.value.trim(),
        "Email: " + email.value.trim(),
        org ? "Organization: " + org : null,
        "Topic: " + topic.options[topic.selectedIndex].text,
        "",
        message.value.trim(),
        "",
        "--",
        "Sent from the PeerSync contact page (" + location.href.split("#")[0] + ")"
      ].filter(function (l) { return l !== null; }).join("\n");

      var href = "mailto:" + encodeURIComponent(mailto) +
        "?subject=" + encodeURIComponent(subject) +
        "&body=" + encodeURIComponent(body);

      result.className = "form-result show";
      result.innerHTML = "<b>Thanks, " + escapeHtml(name.value.trim()) + ".</b> Your mail client should now open with the message prefilled. " +
        "If nothing happens, copy the text below into an email to <code>" + escapeHtml(mailto) + "</code>." +
        "<pre class=\"mono small\" style=\"white-space:pre-wrap;margin:.8rem 0 0;color:var(--text-2)\">" + escapeHtml(body) + "</pre>";
      result.scrollIntoView({ behavior: "smooth", block: "nearest" });

      window.location.href = href;
    });
  }

  function escapeHtml(s) {
    return s.replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c];
    });
  }
})();
