/* HarmonicaPlayer-Revised — 展示页交互（轻量，无依赖） */
(function () {
  "use strict";

  var reduced = window.matchMedia &&
    window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  /* 1. 滚动入场 */
  var items = document.querySelectorAll(".reveal");
  if (reduced || !("IntersectionObserver" in window)) {
    items.forEach(function (el) { el.classList.add("in"); });
  } else {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e, i) {
        if (!e.isIntersecting) return;
        var el = e.target;
        var idx = Number(el.dataset.ri || 0);
        setTimeout(function () { el.classList.add("in"); }, Math.min(idx, 6) * 70);
        io.unobserve(el);
      });
    }, { threshold: 0.12, rootMargin: "0px 0px -60px 0px" });

    var groups = new Map();
    items.forEach(function (el) {
      var parent = el.parentElement;
      var n = groups.get(parent) || 0;
      el.dataset.ri = String(n);
      groups.set(parent, n + 1);
      io.observe(el);
    });
  }

  /* 2. 卡片跟随光标的辉光 */
  if (!reduced && window.matchMedia("(hover:hover)").matches) {
    document.querySelectorAll(".card").forEach(function (card) {
      card.addEventListener("mousemove", function (ev) {
        var r = card.getBoundingClientRect();
        card.style.setProperty("--mx", ((ev.clientX - r.left) / r.width * 100) + "%");
        card.style.setProperty("--my", ((ev.clientY - r.top) / r.height * 100) + "%");
      });
    });
  }

  /* 3. 导航当前区块高亮 */
  var links = Array.prototype.slice.call(document.querySelectorAll(".nav-links a"));
  var sections = links
    .map(function (a) { return document.querySelector(a.getAttribute("href")); })
    .filter(Boolean);

  if (sections.length && "IntersectionObserver" in window) {
    var spy = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (!e.isIntersecting) return;
        links.forEach(function (a) {
          a.style.color = a.getAttribute("href") === "#" + e.target.id ? "#e8eef5" : "";
        });
      });
    }, { rootMargin: "-45% 0px -50% 0px" });
    sections.forEach(function (s) { spy.observe(s); });
  }
})();
