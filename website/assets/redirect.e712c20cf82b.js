const route = navigator.language?.toLowerCase().startsWith("zh") ? "zh-cn" : "en";
const base = document.currentScript?.src.replace(/assets\/redirect\.[^.]+\.js$/, "") ?? "/DropSpace/";
location.replace(`${base}${route}/${location.hash}`);
