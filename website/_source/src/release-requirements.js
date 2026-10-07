// Presentation only: read the selected Release's existing notes without changing any release API.
export function windowsRequirement(body) {
  const matches = [...String(body ?? "").matchAll(/\brequires\s+(?:64-bit\s+)?Windows(?:\s+(10|11))?\s+build\s+(\d+)\s+or\s+later\b/gi)];
  const requirements = new Map(matches.map((match) => [
    `${match[1] ?? ""}:${match[2]}`,
    { version: match[1] ?? "", build: Number(match[2]) }
  ]));
  if (requirements.size !== 1) return null;
  const requirement = [...requirements.values()][0];
  return Number.isSafeInteger(requirement.build) && requirement.build > 0 ? requirement : null;
}

export function stableRequirements(release, locale = "en-US", translate = globalThis.DropSpaceI18n?.t) {
  const requirement = windowsRequirement(release.body);
  const channel = translate("release.stable");
  if (!requirement) return translate("requirements.unknown", { channel, tag: release.tagName, hint: releaseRequirementsHint(locale, translate) });
  const windows = `Windows${requirement.version ? ` ${requirement.version}` : ""}`;
  const build = new Intl.NumberFormat(locale, { useGrouping: false }).format(requirement.build);
  return translate("requirements.minimum", { channel, tag: release.tagName, windows, build });
}

export function releaseRequirementsHint(locale = "en-US", translate = globalThis.DropSpaceI18n?.t) {
  return translate("requirements.hint");
}
