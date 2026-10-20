export function suggestedPlatform(navigatorLike) {
  const platform = navigatorLike.userAgentData?.platform || navigatorLike.platform || "";
  if (/mac/i.test(platform) && (navigatorLike.maxTouchPoints || 0) <= 1) return "macos";
  if (/win/i.test(platform)) return "windows";
  return null;
}

if (typeof document !== "undefined") {
  const platform = suggestedPlatform(navigator);
  document.documentElement.dataset.suggestedPlatform = platform || "unknown";
  for (const link of document.querySelectorAll("[data-platform-option]")) {
    if (link.dataset.platformOption === platform) link.dataset.suggested = "true";
  }
  // A suggestion never hides the other platform or claims CPU/OS compatibility.
  if (platform === "macos") {
    for (const link of document.querySelectorAll("[data-platform-primary]")) {
      link.href = link.dataset.macUrl;
      link.removeAttribute("data-download");
      link.replaceChildren();
      const label = document.createElement("span"); label.dataset.i18n = "mac.nav";
      label.textContent = window.DropSpaceI18n?.t("mac.nav") || "Mac version";
      link.append(label);
    }
    window.DropSpaceI18n?.apply();
  }
}
