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

export function stableRequirements(release, locale = "en") {
  const zh = locale.toLowerCase().startsWith("zh");
  const requirement = windowsRequirement(release.body);
  const prefix = `${zh ? "稳定版" : "Stable"} ${release.tagName}: `;
  if (!requirement) return prefix + releaseRequirementsHint(locale);
  const windows = `Windows${requirement.version ? ` ${requirement.version}` : ""}`;
  return prefix + (zh
    ? `需要 64 位 ${windows} Build ${requirement.build} 或更高版本`
    : `64-bit ${windows} build ${requirement.build} or later`);
}

export function releaseRequirementsHint(locale = "en") {
  return locale.toLowerCase().startsWith("zh")
    ? "64 位 Windows · 系统要求请查看发布说明"
    : "64-bit Windows · See release notes for system requirements";
}
