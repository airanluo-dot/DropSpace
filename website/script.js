const header = document.querySelector("[data-header]");
const demo = document.querySelector("[data-demo]");
const reduced = matchMedia("(prefers-reduced-motion: reduce)");

const zh = {
  "Skip to content": "跳到主要内容",
  "Features": "功能",
  "Download": "下载",
  "Changelog": "更新日志",
  "Drag it. Keep it.": "拖进来。暂存好。",
  "Use it later.": "需要时再取走。",
  "A temporary space for Windows 11—right at the top of your screen.": "Windows 11 的临时空间，就在屏幕顶部。",
  "Download for Windows": "下载 Windows 版",
  "View on GitHub": "在 GitHub 查看",
  "Latest Stable · v0.1.0": "最新稳定版 · v0.1.0",
  "Desktop": "桌面",
  "Drop Ready": "可以放下",
  "Release to add files": "松开即可添加文件",
  "Temporary Space": "临时空间",
  "Ready when you are": "随时等你取用",
  "3 items": "3 个项目",
  "3 recent items": "最近 3 个项目",
  "Reference · source unchanged": "文件引用 · 原文件不变",
  "Removing a reference never deletes the original file.": "移除引用绝不会删除原文件。",
  "1. Drag from anywhere": "1. 从任意位置拖动",
  "2. Drop at the top": "2. 放到屏幕顶部",
  "3. Use it later": "3. 需要时再使用",
  "01 / TEMPORARY SPACE": "01 / 临时空间",
  "Without the": "临时存放，",
  "temporary mess.": "不再杂乱。",
  "DropSpace keeps file and folder references at your fingertips without moving or duplicating the originals. Your files stay exactly where you left them.": "DropSpace 将文件与文件夹引用放在触手可及的位置，不移动、也不复制原文件。文件仍留在原来的位置。",
  "Original stays in place": "原文件保持原位",
  "Nothing is moved or copied.": "不会移动或复制任何内容。",
  "02 / TOP-SCREEN ACCESS": "02 / 屏幕顶部访问",
  "Two shapes.": "两种外形。",
  "One quick workspace.": "同一个快捷空间。",
  "Choose Dynamic Island or Notch. Both move through the same clear states: Compact, Drop Ready, Expanded.": "选择灵动岛或刘海模式。两者都拥有相同的清晰状态：紧凑、可以放下、展开。",
  "Dynamic Island": "灵动岛",
  "Notch": "刘海",
  "Compact": "紧凑",
  "Expanded": "展开",
  "03 / CLIPBOARD HISTORY": "03 / 剪贴板历史",
  "History that": "真正有用的",
  "stays useful.": "剪贴板历史。",
  "Find copied text, links, images, files and folders. Pause recording when you need to, pin what matters, and search everything in one place.": "查找复制过的文本、链接、图片、文件和文件夹。随时暂停记录、固定重要内容，并在一个地方搜索全部历史。",
  "Consecutive duplicates collapse.": "连续重复内容会自动合并。",
  "Useful A → B → A history stays.": "有意义的 A → B → A 历史仍会保留。",
  "Clipboard History": "剪贴板历史",
  "All": "全部",
  "Text": "文本",
  "Links": "链接",
  "Images": "图片",
  "Files": "文件",
  "Pinned": "已固定",
  "04 / DRAG & DROP": "04 / 拖放",
  "Built for the drag.": "为拖放而生。",
  "Explorer, Desktop, Compact, Expanded, or the main window—DropSpace meets the file where your workflow already is.": "无论资源管理器、桌面、紧凑模式、展开模式还是主窗口，DropSpace 都能融入你已有的文件工作流。",
  "Explorer / Desktop": "资源管理器 / 桌面",
  "→ Compact Island": "→ 紧凑灵动岛",
  "→ Expanded Island": "→ 展开灵动岛",
  "Main Window": "主窗口",
  "Drag in / out": "拖入 / 拖出",
  "Search": "搜索",
  "Pinned items": "固定项目",
  "Folders": "文件夹",
  "Tray access": "托盘访问",
  "Start with Windows": "随 Windows 启动",
  "Stable & Preview updates": "稳定版与预览版更新",
  "DOWNLOAD": "下载",
  "Ready when": "准备就绪，",
  "you are.": "随时可用。",
  "Windows 11 build 26100 or later": "需要 Windows 11 版本 26100 或更高",
  "View all releases": "查看全部版本",
  "SHA-256 checksums": "SHA-256 校验值",
  "Download Installer": "下载安装程序",
  "Recommended for most users": "推荐大多数用户选择",
  "RECOMMENDED": "推荐",
  "Portable": "便携版",
  "Run without installing": "无需安装即可运行",
  "MSIX": "MSIX",
  "Alternative unsigned package": "备用的未签名软件包",
  "WHAT'S NEW": "最新变化",
  "First Stable.": "首个稳定版。",
  "v0.1.0 — First Stable Release": "v0.1.0 — 首个稳定版本",
  "August 11, 2026": "2026 年 8 月 11 日",
  "Final official branding": "最终正式品牌视觉",
  "Temporary Space with Dynamic Island or Notch": "支持灵动岛或刘海模式的临时空间",
  "Clipboard History with consecutive duplicate suppression": "带连续重复抑制的剪贴板历史",
  "In-app Stable and Preview update channels": "应用内稳定版与预览版更新通道",
  "Installer, Portable, and MSIX downloads": "安装程序、便携版与 MSIX 下载",
  "View full changelog": "查看完整更新日志",
  "OPEN SOURCE": "开源",
  "Open by design.": "从设计之初便开放。",
  "DropSpace is built in the open, with its code, decisions, privacy model and release history available to everyone.": "DropSpace 在开放环境中构建，代码、设计决策、隐私模型和发布历史均向所有人公开。",
  "View Source": "查看源代码",
  "Star on GitHub": "在 GitHub 加星",
  "Report an Issue": "报告问题",
  "FAQ": "常见问题",
  "Good to know.": "你可能想知道。",
  "What is DropSpace?": "DropSpace 是什么？",
  "A local-first Windows 11 workspace for temporarily holding file references and recent clipboard content, with a main window and a top-center Dynamic Island or Notch.": "一个本地优先的 Windows 11 工作空间，用于临时保存文件引用和最近的剪贴板内容，并提供主窗口与屏幕顶部的灵动岛或刘海界面。",
  "Does DropSpace move my files?": "DropSpace 会移动我的文件吗？",
  "No. Temporary Space stores references. Adding or removing a reference does not move, copy, or delete the original file.": "不会。临时空间只保存引用。添加或移除引用都不会移动、复制或删除原文件。",
  "Can I add folders?": "可以添加文件夹吗？",
  "Yes. DropSpace can hold file and folder references through drag and drop or its picker.": "可以。你可以通过拖放或选择器将文件和文件夹引用加入 DropSpace。",
  "What is Clipboard History?": "什么是剪贴板历史？",
  "A local history of captured text, URLs, images, files, and folders. You can search, pin, pause, clear, and apply retention limits.": "保存在本地的文本、网址、图片、文件和文件夹历史。你可以搜索、固定、暂停、清除并设置保留限制。",
  "What's the difference between Installer and Portable?": "安装版和便携版有什么区别？",
  "The recommended per-user Installer adds Start Menu integration, an uninstaller, and in-place update support. Portable runs directly without installation; updates are notify/download-only.": "推荐的个人用户安装版提供开始菜单入口、独立卸载程序和原位更新；便携版无需安装即可运行，更新仅提供通知和下载。",
  "Does DropSpace support Windows 10?": "DropSpace 支持 Windows 10 吗？",
  "No. The current release requires Windows 11 build 26100 or later.": "不支持。当前版本需要 Windows 11 版本 26100 或更高。",
  "What about the Windows 11 Drop Tray?": "Windows 11 顶部拖放托盘怎么办？",
  "Windows may own the same top-edge drag gesture. DropSpace Settings links to the official Multitasking page; direct drops onto visible Compact and Expanded surfaces remain supported.": "Windows 可能会占用相同的顶部拖动手势。DropSpace 设置会链接到官方多任务页面；你仍可直接拖放到可见的紧凑或展开界面。",
  "Does DropSpace upload clipboard content?": "DropSpace 会上传剪贴板内容吗？",
  "No. Ordinary content features use local storage. If update checks are enabled, DropSpace contacts only public GitHub Release metadata and official GitHub asset URLs.": "不会。常规内容功能只使用本地存储。启用更新检查后，DropSpace 只会访问公开的 GitHub Release 元数据和官方 GitHub 资源地址。",
  "How do updates work?": "更新如何工作？",
  "Stable receives Stable releases. Preview can receive Stable and Preview releases. DropSpace verifies download size and SHA-256; the current unsigned release requires explicit user action to install.": "稳定通道只接收稳定版本；预览通道可接收稳定版和预览版。DropSpace 会验证下载大小和 SHA-256；当前未签名版本需要用户明确操作后才能安装。",
  "Releases": "发布版本",
  "License": "许可证",
  "Privacy": "隐私",
  "All releases": "全部版本",
  "What's new.": "更新内容。",
  "Stable releases for most users, plus an optional Preview channel for early builds. The main download always follows Stable.": "稳定版适合大多数用户，预览通道用于提前体验新构建；网站主下载始终指向稳定版。",
  "Stable": "稳定版",
  "First Stable Release": "首个稳定版本",
  "Full release notes": "完整发布说明",
  "Preview channel": "预览通道",
  "Preview releases remain available through GitHub Releases and the optional in-app Preview channel. They are never the default website download.": "预览版本仍可通过 GitHub Releases 和应用内可选预览通道获取，但绝不会成为网站默认下载。",
  "Download v0.1.0": "下载 v0.1.0"
};

function translatePage(language) {
  const isZh = language === "zh";
  document.documentElement.lang = isZh ? "zh-CN" : "en";
  if (!isZh) return;
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const value = node.nodeValue;
    const trimmed = value?.trim();
    if (!trimmed || !zh[trimmed]) continue;
    node.nodeValue = value.replace(trimmed, zh[trimmed]);
  }
  document.title = location.pathname.includes("changelog") ? "更新日志 — DropSpace" : "DropSpace — Windows 临时空间";
  document.querySelector('meta[name="description"]')?.setAttribute("content", "将文件拖到 Windows 11 屏幕顶部，临时存放，需要时再取回。");
  document.querySelectorAll("[data-language-switch]").forEach((button) => {
    button.textContent = "EN";
    button.setAttribute("aria-label", "Switch to English");
  });
}

const requestedLanguage = new URLSearchParams(location.search).get("lang");
const savedLanguage = localStorage.getItem("dropspace-language");
const language = requestedLanguage === "en" || requestedLanguage === "zh"
  ? requestedLanguage
  : savedLanguage ?? (navigator.language.toLowerCase().startsWith("zh") ? "zh" : "en");
translatePage(language);
document.querySelectorAll("[data-language-switch]").forEach((button) => {
  button.addEventListener("click", () => {
    const next = document.documentElement.lang === "zh-CN" ? "en" : "zh";
    localStorage.setItem("dropspace-language", next);
    const url = new URL(location.href);
    url.searchParams.set("lang", next);
    location.href = url;
  });
});

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
