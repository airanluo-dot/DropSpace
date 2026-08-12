const header = document.querySelector("[data-header]");
const demo = document.querySelector("[data-demo]");
const reduced = matchMedia("(prefers-reduced-motion: reduce)");

addEventListener("scroll", () => header?.classList.toggle("scrolled", scrollY > 18), { passive: true });

if (demo) {
  let timer;
  const run = () => {
    if (reduced.matches) {
      demo.dataset.state = "expanded";
      return;
    }
    const states = [["idle", 0], ["ready", 2500], ["expanded", 3900], ["idle", 6500]];
    let index = 0;
    const step = () => {
      demo.dataset.state = states[index][0];
      const next = states[(index + 1) % states.length];
      const delay = index === states.length - 1 ? 700 : next[1] - states[index][1];
      index = (index + 1) % states.length;
      timer = setTimeout(step, delay);
    };
    step();
  };
  run();
  reduced.addEventListener("change", () => { clearTimeout(timer); run(); });
}

const stage = document.querySelector("[data-mode-stage]");
document.querySelectorAll("[data-mode]").forEach((button) => {
  button.addEventListener("click", () => {
    document.querySelectorAll("[data-mode]").forEach((item) => {
      const active = item === button;
      item.classList.toggle("active", active);
      item.setAttribute("aria-pressed", String(active));
    });
    stage?.classList.toggle("notch", button.dataset.mode === "notch");
  });
});

const observer = new IntersectionObserver((entries) => {
  for (const entry of entries) if (entry.isIntersecting) entry.target.classList.add("revealed");
}, { threshold: 0.12 });
document.querySelectorAll("main section").forEach((section) => observer.observe(section));

const systemCheck = document.querySelector("[data-system-check]");
if (systemCheck) {
  const platform = navigator.userAgentData?.platform ?? navigator.platform ?? "";
  const isWindows = /windows|win32|win64/i.test(platform);
  systemCheck.textContent = isWindows ? systemCheck.dataset.windows : systemCheck.dataset.other;
  systemCheck.dataset.result = isWindows ? "windows" : "other";
}
