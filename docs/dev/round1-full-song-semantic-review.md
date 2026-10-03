# Full-song v3 semantic review: complete line-by-line evidence

Review date: 2026-10-01. This is an assistant manual reading of all 144 source/output pairs, not a professional bilingual certification or statistical quality benchmark. Labels distinguish clear meaning changes from context-dependent interpretations. No model output was altered. Structural validation is documented separately in the full-song QA report. IDs are zero-based.

Important: the four parallel 12-line verses are not literal copies. Each output was compared with its own language’s source, not automatically with the English verse.

## Release-quality status: blocked

The current release candidate has known serious semantic errors in the reviewed fixture. Standard Chinese→English reverses leaving the silver key into keeping it (ID 45); compact Korean→Chinese replaces scarf with headband (ID 33). A second QA-only attempt using unchanged local context plus a generic fidelity requirement reproduced both errors. Under the approved no-known-serious-semantic-error release criterion, native structural success and an AI-mistranslation disclaimer do not clear this quality gate.

These are observed model-output limitations, not demonstrated application text-swapping bugs. Original-language ambiguity is separately labeled and is not the basis of this blocking conclusion.

## standard → en-US

| ID | Source | Actual output | Review |
|---:|---|---|---|
| 0 | I fold the morning into a paper boat. | I fold the morning into a paper boat. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 1 | Your blue cup waits beside the window. | Your blue cup waits beside the window. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 2 | I did not promise to forget your name. | I did not promise to forget your name. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 3 | You said the northern road was closed. | You said the northern road was closed. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 4 | We carry seven seeds across the bridge. | We carry seven seeds across the bridge. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 5 | No lantern can replace the summer sun. | No lantern can replace the summer sun. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 6 | If you return, knock softly twice. | If you return, knock softly twice. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 7 | I hear the train but cannot see its lights. | I hear the train but cannot see its lights. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 8 | The quiet room belongs to neither of us. | The quiet room belongs to neither of us. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 9 | Keep the red scarf; leave the silver key. | Keep the red scarf; leave the silver key. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 10 | Tomorrow I will choose a different path. | Tomorrow I will choose a different path. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 11 | Our little river still remembers rain. | Our little river still remembers rain. | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 12 | 朝の光を紙の舟に折る。 | Fold the morning light into the paper boat. | **Context-dependent**: Japanese declarative with omitted subject becomes English imperative “Fold”; may change narrative voice. The source does not explicitly identify a subject. |
| 13 | 青いカップが窓辺で君を待つ。 | The blue cup waits for you by the window. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 14 | 君の名前を忘れるとは約束していない。 | I did not promise to forget your name. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 15 | 北へ続く道は閉じたと君は言った。 | You said the northern path was closed. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 16 | 七つの種を持って橋を渡る。 | Cross the bridge with seven seeds. | **Context-dependent**: Japanese declarative with omitted subject becomes imperative “Cross”; seven individual seeds is preserved. |
| 17 | 灯りは夏の太陽の代わりにはならない。 | The light cannot replace the summer sun. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 18 | 戻るなら、そっと二回ノックして。 | If you return, knock gently twice. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 19 | 列車の音は聞こえるが、光は見えない。 | The train sounds are heard, but no light is seen. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 20 | 静かな部屋は私たちのどちらのものでもない。 | The quiet room belongs to neither of us. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 21 | 赤いスカーフを持って、銀の鍵を置いて。 | Keep the red scarf; leave the silver key. | **Acceptable wording**: 持って is rendered “Keep”; holding/taking the scarf and leaving the key remain coherent in this lyric context. |
| 22 | 明日は別の道を選ぶ。 | Tomorrow I will choose a different path. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 23 | 小さな川はまだ雨を覚えている。 | Our little river still remembers rain. | **Detail drift**: Adds “Our” ownership; the Japanese source says “the little river” without explicit ownership. Nearby parallel English lines may supply context, but this line alone does not. |
| 24 | 아침 햇살을 종이배에 접어 담아. | I fold the morning sunlight into a paper boat. | **Context-dependent**: Adds “I” to Korean with omitted subject; plausible in the parallel verse, not explicitly stated in this source line. |
| 25 | 파란 컵이 창가에서 너를 기다려. | The blue cup waits for you by the window. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 26 | 네 이름을 잊겠다고 약속하지 않았어. | I did not promise to forget your name. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 27 | 북쪽 길이 닫혔다고 네가 말했어. | You said the northern path is closed. | **Acceptable wording**: Present “is closed” inside reported speech may refer to a continuing condition; no clear reversal. |
| 28 | 우리는 씨앗 일곱 개를 들고 다리를 건너. | We carry seven seeds and cross the bridge. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 29 | 등불은 여름 태양을 대신할 수 없어. | The lantern cannot replace the summer sun. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 30 | 돌아오면 조용히 두 번 두드려. | When we return, gently knock twice. | **Context-dependent concern**: “When we return” introduces inclusive “we” absent from Korean 돌아오면. With the ensuing knock request and parallel verses, “If/when you return” is more natural. Korean leaves the subject implicit, so this is unsupported resolution rather than a proven explicit-pronoun reversal. |
| 31 | 기차 소리는 들리지만 불빛은 보이지 않아. | The train sounds are heard, but no light is seen. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 32 | 고요한 방은 우리 누구의 것도 아니야. | The quiet room is not anyone's. | **Material detail error**: 우리 누구의 것도 아니야 means not belonging to any of us; “not anyone’s” expands the negation to all people, losing the bounded group. |
| 33 | 빨간 목도리는 가져가고 은색 열쇠는 두고 가. | Take the red scarf, but leave the silver key. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 34 | 내일 나는 다른 길을 고를 거야. | Tomorrow I will choose a different path. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 35 | 작은 강은 아직 비를 기억해. | The small river still remembers the rain. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 36 | 我把晨光折进一只纸船。 | I fold the morning light into a paper boat. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 37 | 你的蓝杯子仍在窗边等候。 | Your blue cup is still waiting by the window. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 38 | 我并没有答应忘记你的名字。 | I have not promised to forget your name. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 39 | 你说向北的路已经关闭。 | You say the road north is closed. | **Acceptable wording**: Chinese 你说 has no overt tense marker; English present “You say” is not an unambiguous tense error. |
| 40 | 我们带着七粒种子走过桥。 | We walk across the bridge with seven seeds. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 41 | 没有灯笼能代替夏天的太阳。 | No lantern can replace the summer sun. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 42 | 如果你回来，请轻轻敲两下门。 | If you come back, please knock gently on the door. | **Material detail error**: Drops 两下 (“twice”); condition and quiet knocking remain, but the explicit count is lost. |
| 43 | 我听见火车，却看不见灯光。 | I hear the train, but I cannot see the lights. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 44 | 安静的房间不属于我们任何一个人。 | The quiet room does not belong to any of us. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 45 | 带走红围巾，留下银钥匙。 | Take the red scarf, keep the silver key. | **Major semantic error**: “留下银钥匙” means leave the silver key behind; “keep the silver key” reverses the requested handling of the key. |
| 46 | 明天我会选择另一条路。 | Tomorrow I will choose a different path. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 47 | 我们的小河仍然记得雨。 | Our small river still remembers the rain. | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |

## standard → zh-CN

| ID | Source | Actual output | Review |
|---:|---|---|---|
| 0 | I fold the morning into a paper boat. | 我把早晨折成纸船。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 1 | Your blue cup waits beside the window. | 你的蓝色杯子在窗边等待。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 2 | I did not promise to forget your name. | 我没有承诺会忘记你的名字。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 3 | You said the northern road was closed. | 你说北方的路已经关闭了。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 4 | We carry seven seeds across the bridge. | 我们带着七颗种子穿过桥梁。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 5 | No lantern can replace the summer sun. | 没有灯笼可以替代夏日的阳光。 | **Acceptable wording**: “Summer sun” becomes 夏日的阳光 (“summer sunlight”): close imagery, although less literal. |
| 6 | If you return, knock softly twice. | 如果你回来，轻轻敲两下门。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 7 | I hear the train but cannot see its lights. | 我听到火车的声音，却看不清灯光。 | **Material detail error**: “Cannot see” becomes 看不清 (“cannot see clearly”), weakening complete non-visibility to poor visibility. |
| 8 | The quiet room belongs to neither of us. | 安静的房间不属于我们中的任何一人。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 9 | Keep the red scarf; leave the silver key. | 保留红色围巾，留下银色钥匙。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 10 | Tomorrow I will choose a different path. | 明天我会选择另一条路。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 11 | Our little river still remembers rain. | 我们这条小河仍然记得雨水。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 12 | 朝の光を紙の舟に折る。 | 将晨光折入纸舟。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 13 | 青いカップが窓辺で君を待つ。 | 蓝色杯子在窗边等待你。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 14 | 君の名前を忘れるとは約束していない。 | 我没有承诺会忘记你的名字。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 15 | 北へ続く道は閉じたと君は言った。 | 你说通往北方的路已经关闭了。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 16 | 七つの種を持って橋を渡る。 | 携七种种子渡过桥梁。 | **Material detail error**: 七つの種 counts seven seeds; 七种种子 means seven kinds/types of seeds, changing the counted unit. |
| 17 | 灯りは夏の太陽の代わりにはならない。 | 灯光无法替代夏日的阳光。 | **Acceptable wording**: 夏の太陽 becomes 夏日的阳光, a close sun/sunlight paraphrase. |
| 18 | 戻るなら、そっと二回ノックして。 | 若想返回，轻轻敲两下门吧。 | **Detail drift**: 戻るなら (“if you return”) becomes 若想返回 (“if you want to return”), adding desire to the condition. |
| 19 | 列車の音は聞こえるが、光は見えない。 | 能听到火车的声音，却看不见光芒。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 20 | 静かな部屋は私たちのどちらのものでもない。 | 安静的房间不属于我们中的任何一人。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 21 | 赤いスカーフを持って、銀の鍵を置いて。 | 拿着红色围巾，把银钥匙留下。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 22 | 明日は別の道を選ぶ。 | 明天我将选择另一条路。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 23 | 小さな川はまだ雨を覚えている。 | 小河流着记忆中的雨。 | **Detail drift**: “The little river still remembers rain” becomes “the river flows with rain from memory”; modifies the metaphor and loses explicit “still”. |
| 24 | 아침 햇살을 종이배에 접어 담아. | 将晨光折进纸船。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 25 | 파란 컵이 창가에서 너를 기다려. | 蓝色杯子在窗边等候你。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 26 | 네 이름을 잊겠다고 약속하지 않았어. | 我没有承诺忘记你的名字。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 27 | 북쪽 길이 닫혔다고 네가 말했어. | 你说向北的路已经关闭了。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 28 | 우리는 씨앗 일곱 개를 들고 다리를 건너. | 我们带着七粒种子过桥。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 29 | 등불은 여름 태양을 대신할 수 없어. | 灯笼无法替代夏日的阳光。 | **Acceptable wording**: Summer sun becomes 夏日的阳光; negation and replacement relation remain. |
| 30 | 돌아오면 조용히 두 번 두드려. | 回来时轻轻敲两下。 | **Acceptable wording**: Korean -면 allows conditional/temporal reading; 回来时 (“when returning”) is reasonable and preserves two knocks. |
| 31 | 기차 소리는 들리지만 불빛은 보이지 않아. | 能听到火车声，却看不见灯光。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 32 | 고요한 방은 우리 누구의 것도 아니야. | 安静的房间不是任何人的。 | **Material detail error**: “None of us owns the quiet room” broadens to “not anyone’s”; bounded group is lost. |
| 33 | 빨간 목도리는 가져가고 은색 열쇠는 두고 가. | 带上红色围巾，留下银色钥匙。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 34 | 내일 나는 다른 길을 고를 거야. | 明天我会选择另一条路。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 35 | 작은 강은 아직 비를 기억해. | 小河流着记忆中的雨。 | **Detail drift**: Korean “small river still remembers rain” becomes “river flows with rain from memory”, introducing a different poetic image and losing explicit “still”. |
| 36 | 我把晨光折进一只纸船。 | 我把晨光折进一只纸船。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 37 | 你的蓝杯子仍在窗边等候。 | 你的蓝杯子仍在窗边等候。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 38 | 我并没有答应忘记你的名字。 | 我并没有答应忘记你的名字。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 39 | 你说向北的路已经关闭。 | 你说向北的路已经关闭。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 40 | 我们带着七粒种子走过桥。 | 我们带着七粒种子走过桥。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 41 | 没有灯笼能代替夏天的太阳。 | 没有灯笼能代替夏天的太阳。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 42 | 如果你回来，请轻轻敲两下门。 | 如果你回来，请轻轻敲两下门。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 43 | 我听见火车，却看不见灯光。 | 我听见火车，却看不见灯光。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 44 | 安静的房间不属于我们任何一个人。 | 安静的房间不属于我们任何一个人。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 45 | 带走红围巾，留下银钥匙。 | 带走红围巾，留下银钥匙。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 46 | 明天我会选择另一条路。 | 明天我会选择另一条路。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 47 | 我们的小河仍然记得雨。 | 我们的小河仍然记得雨。 | **Exact same-language copy**: Source retained exactly; no translation needed. |

## compact → zh-CN

| ID | Source | Actual output | Review |
|---:|---|---|---|
| 0 | I fold the morning into a paper boat. | 将清晨折叠成纸船。 | **Acceptable wording**: Explicit English “I” is omitted in a natural Chinese null-subject line; the surrounding verse can carry the speaker, but the line is less explicit. |
| 1 | Your blue cup waits beside the window. | 蓝色杯子在窗边等待你。 | **Detail drift**: “Your blue cup waits beside the window” becomes “the blue cup waits for you”; drops possessive ownership and adds a waiting recipient. |
| 2 | I did not promise to forget your name. | 没有承诺忘记你的名字。 | **Acceptable wording**: Chinese omits explicit “I” but preserves the crucial “did not promise to forget” negation scope. |
| 3 | You said the northern road was closed. | 你说北方的路已经关闭了。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 4 | We carry seven seeds across the bridge. | 我们带着七颗种子穿过桥梁。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 5 | No lantern can replace the summer sun. | 没有灯笼能替代夏日的阳光。 | **Acceptable wording**: Summer sun becomes summer sunlight; central negation and comparison remain. |
| 6 | If you return, knock softly twice. | 如果你回来，轻轻敲两下门。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 7 | I hear the train but cannot see its lights. | 我听到火车的声音，却看不见它的灯光。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 8 | The quiet room belongs to neither of us. | 安静的房间不属于我们任何一方。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 9 | Keep the red scarf; leave the silver key. | 保留红色围巾，留下银色钥匙。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 10 | Tomorrow I will choose a different path. | 明天我会选择一条不同的路。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 11 | Our little river still remembers rain. | 我们的小河流仍然记得雨水。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 12 | 朝の光を紙の舟に折る。 | 将晨光折入纸舟。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 13 | 青いカップが窓辺で君を待つ。 | 蓝色杯子在窗边等待你。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 14 | 君の名前を忘れるとは約束していない。 | 没有承诺忘记你的名字。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 15 | 北へ続く道は閉じたと君は言った。 | 你说通往北方的路已经关闭了。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 16 | 七つの種を持って橋を渡る。 | 携带七种种子过桥。 | **Material detail error**: Seven seeds becomes seven kinds/types of seeds (七种种子), changing the counted unit. |
| 17 | 灯りは夏の太陽の代わりにはならない。 | 灯光不会成为夏日的替代。 | **Material detail error**: Drops 太陽 (“sun”): 灯光不会成为夏日的替代 means light will not replace summer, rather than the summer sun. |
| 18 | 戻るなら、そっと二回ノックして。 | 若返回，轻轻敲两下。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 19 | 列車の音は聞こえるが、光は見えない。 | 火车声音可听，光芒却不可见。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 20 | 静かな部屋は私たちのどちらのものでもない。 | 安静的房间不属于我们任何一方。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 21 | 赤いスカーフを持って、銀の鍵を置いて。 | 拿着红围巾，留下银钥匙。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 22 | 明日は別の道を選ぶ。 | 明天将选择另一条路。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 23 | 小さな川はまだ雨を覚えている。 | 小河流水仍记得雨水。 | **Acceptable wording**: 小河流水 slightly expands the river image; “still remembers rain” remains. |
| 24 | 아침 햇살을 종이배에 접어 담아. | 晨光折进一只纸船。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 25 | 파란 컵이 창가에서 너를 기다려. | 你的蓝杯子仍在窗边等候。 | **Detail drift**: Korean “the blue cup waits for you by the window” becomes “your blue cup is still waiting by the window”; adds ownership and “still”, drops explicit waiting recipient. Similar to Chinese context line 37, but context-copy causation is not proven. |
| 26 | 네 이름을 잊겠다고 약속하지 않았어. | 我并没有答应忘记你的名字。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 27 | 북쪽 길이 닫혔다고 네가 말했어. | 你说向北的路已经关闭。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 28 | 우리는 씨앗 일곱 개를 들고 다리를 건너. | 我们带着七颗种子过桥。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 29 | 등불은 여름 태양을 대신할 수 없어. | 等灯无法替代夏季的太阳。 | **Material lexical error**: 등불 (“lamp/lantern”) is rendered 等灯, an ill-formed/wrong noun. The negative replacement relation is retained; this is not a polarity reversal. |
| 30 | 돌아오면 조용히 두 번 두드려. | 回来时安静地敲两下门。 | **Acceptable wording**: Quietly knocking twice is preserved; adding 门 is a plausible ellipsis resolution in context. |
| 31 | 기차 소리는 들리지만 불빛은 보이지 않아. | 能听到火车的声音，却看不见灯光。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 32 | 고요한 방은 우리 누구의 것도 아니야. | 宁静的房间不是任何人的所有物。 | **Material detail error**: 우리 (“us”) is lost, broadening non-ownership from our group to anyone. |
| 33 | 빨간 목도리는 가져가고 은색 열쇠는 두고 가. | 带走红色头带，留下银色钥匙。 | **Major semantic error**: 목도리 is a scarf worn around the neck; 头带 is a headband, changing the object to take. |
| 34 | 내일 나는 다른 길을 고를 거야. | 明天我会选择另一条路。 | **Acceptable wording**: No clear material change found in this line; subject, action and explicit constraints are retained or naturally implicit. |
| 35 | 작은 강은 아직 비를 기억해. | 小河还记着雨水的记忆。 | **Acceptable but awkward**: “Still remembers rain” becomes “still remembers the memory of rain”; redundant poetic phrasing, without a clear reversal. |
| 36 | 我把晨光折进一只纸船。 | 我把晨光折进一只纸船。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 37 | 你的蓝杯子仍在窗边等候。 | 你的蓝杯子仍在窗边等候。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 38 | 我并没有答应忘记你的名字。 | 我并没有答应忘记你的名字。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 39 | 你说向北的路已经关闭。 | 你说向北的路已经关闭。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 40 | 我们带着七粒种子走过桥。 | 我们带着七粒种子走过桥。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 41 | 没有灯笼能代替夏天的太阳。 | 没有灯笼能代替夏天的太阳。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 42 | 如果你回来，请轻轻敲两下门。 | 如果你回来，请轻轻敲两下门。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 43 | 我听见火车，却看不见灯光。 | 我听见火车，却看不见灯光。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 44 | 安静的房间不属于我们任何一个人。 | 安静的房间不属于我们任何一个人。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 45 | 带走红围巾，留下银钥匙。 | 带走红围巾，留下银钥匙。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 46 | 明天我会选择另一条路。 | 明天我会选择另一条路。 | **Exact same-language copy**: Source retained exactly; no translation needed. |
| 47 | 我们的小河仍然记得雨。 | 我们的小河仍然记得雨。 | **Exact same-language copy**: Source retained exactly; no translation needed. |

## Cross-cutting findings

- All 36 same-target-language source lines were copied exactly (12 in each result).
- No clear entire-line swaps were found by reading all 144 pairs. Shared motifs across the parallel verses make this a limited alignment probe; exact IDs alone would not detect text swapped between correct IDs.
- The “did not promise to forget” scope is retained across all translated equivalents. No clear affirmative/negative reversal was found. Some other negations have weakened scope: source “cannot see” versus “cannot see clearly”, and “none of us” versus “no one”.
- Explicit quantity failures: standard English ID 42 omits two knocks; both Chinese models at ID 16 turn seven individual seeds into seven kinds of seeds. Other seed and two-knock counts were retained.
- Clear major local meaning errors: standard English ID 45 reverses leave versus keep for the key; compact Chinese ID 33 replaces scarf with headband.
- Context-dependent concerns are recorded without calling them proven reversals: Japanese omitted subjects rendered as English imperatives; Korean return-clause subject resolved to “we”; poetic expansions and speaker ellipsis.
- This review does not establish model-wide error rates, compare model quality statistically, or certify professional translation quality.

## Diagnostic distinction

- Earlier context overproduction/empty arrays were failures of the generation output contract, addressed in the production v3 constrained decoder.
- Wrong leave/keep actions, wrong objects, omitted quantities and wrong-language text exist in raw native outputs. This evidence locates them at the model/prompt capability boundary; it does not show application postprocessing swapping text. Structural acceptance is not semantic approval.
- Original-language ambiguity remains separate: Korean omitted subjects and Japanese omitted narrative subjects support more than one reading, so those concerns are not labeled explicit pronoun or polarity reversals.

## Full-context + generic fidelity instruction experiment

Two QA-only candidate batches retained the production v3 exact-ID schema and runner limits. Only the background expanded to all 36 non-task lines of the same 48-line song, without IDs, and one generic sentence requested preservation of quantities, conditions, negation, concrete objects and actions with no additions/omissions. There were no word-specific replacements and no product-source edits. Runs were sequential, reused existing models, and preserved all actual output. This is one observation per candidate with the runner's normal sampling settings, not a repeated fixed-seed controlled benchmark.

### Outcome

- Standard Chinese→English, IDs 36–47: strict-valid, 40.826 s (baseline 29.220 s). The missing two-knock quantity and reversed key action improved: `twice` and `leave the silver key` were restored. However ID 37 now omits 仍/“still”, which the baseline preserved. Much wording matches the parallel English background. This supports a narrow improvement, not a blanket quality fix.
- Compact Korean→Chinese, IDs 24–35: strict-valid, 50.639 s (baseline shown below). Eight outputs are Japanese, identical to corresponding Japanese background lines: IDs 25, 26, 27, 29, 30, 31, 32 and 33. That is a severe target-language failure despite structurally correct IDs. A headband error disappearing because the whole line was copied in Japanese is not a successful Chinese correction.
- Both individual candidates stayed under the existing 60-second call cap. Their full-song latency and reliability were not tested. Expanding all context should not be accepted as a general semantic fix based on these results.

### Complete compared outputs

#### standard / en-US, baseline 29.220 s → candidate 40.826 s

| ID | Source | Baseline output | Full-context candidate |
|---:|---|---|---|
| 36 | 我把晨光折进一只纸船。 | I fold the morning light into a paper boat. | I fold the morning light into a paper boat. |
| 37 | 你的蓝杯子仍在窗边等候。 | Your blue cup is still waiting by the window. | Your blue cup waits beside the window. |
| 38 | 我并没有答应忘记你的名字。 | I have not promised to forget your name. | I did not promise to forget your name. |
| 39 | 你说向北的路已经关闭。 | You say the road north is closed. | You said the northern road was closed. |
| 40 | 我们带着七粒种子走过桥。 | We walk across the bridge with seven seeds. | We carry seven seeds across the bridge. |
| 41 | 没有灯笼能代替夏天的太阳。 | No lantern can replace the summer sun. | No lantern can replace the summer sun. |
| 42 | 如果你回来，请轻轻敲两下门。 | If you come back, please knock gently on the door. | If you return, knock softly twice. |
| 43 | 我听见火车，却看不见灯光。 | I hear the train, but I cannot see the lights. | I hear the train but cannot see its lights. |
| 44 | 安静的房间不属于我们任何一个人。 | The quiet room does not belong to any of us. | The quiet room belongs to neither of us. |
| 45 | 带走红围巾，留下银钥匙。 | Take the red scarf, keep the silver key. | Keep the red scarf; leave the silver key. |
| 46 | 明天我会选择另一条路。 | Tomorrow I will choose a different path. | Tomorrow I will choose a different path. |
| 47 | 我们的小河仍然记得雨。 | Our small river still remembers the rain. | Our little river still remembers rain. |


#### compact / zh-CN, baseline 35.889 s → candidate 50.639 s

| ID | Source | Baseline output | Full-context candidate |
|---:|---|---|---|
| 24 | 아침 햇살을 종이배에 접어 담아. | 晨光折进一只纸船。 | 晨光折进一只纸船。 |
| 25 | 파란 컵이 창가에서 너를 기다려. | 你的蓝杯子仍在窗边等候。 | 青いカップが窓辺で君を待つ。 |
| 26 | 네 이름을 잊겠다고 약속하지 않았어. | 我并没有答应忘记你的名字。 | 君の名前を忘れるとは約束していない。 |
| 27 | 북쪽 길이 닫혔다고 네가 말했어. | 你说向北的路已经关闭。 | 北へ続く道は閉じたと君は言った。 |
| 28 | 우리는 씨앗 일곱 개를 들고 다리를 건너. | 我们带着七颗种子过桥。 | 我们带着七粒种子走过桥。 |
| 29 | 등불은 여름 태양을 대신할 수 없어. | 等灯无法替代夏季的太阳。 | 灯りは夏の太陽の代わりにはならない。 |
| 30 | 돌아오면 조용히 두 번 두드려. | 回来时安静地敲两下门。 | 戻るなら、そっと二回ノックして。 |
| 31 | 기차 소리는 들리지만 불빛은 보이지 않아. | 能听到火车的声音，却看不见灯光。 | 列車の音は聞こえるが、光は見えない。 |
| 32 | 고요한 방은 우리 누구의 것도 아니야. | 宁静的房间不是任何人的所有物。 | 静かな部屋は私たちのどちらのものでもない。 |
| 33 | 빨간 목도리는 가져가고 은색 열쇠는 두고 가. | 带走红色头带，留下银色钥匙。 | 赤いスカーフを持って、銀の鍵を置いて。 |
| 34 | 내일 나는 다른 길을 고를 거야. | 明天我会选择另一条路。 | 明天我会选择另一条路。 |
| 35 | 작은 강은 아직 비를 기억해. | 小河还记着雨水的记忆。 | 我们的小河仍然记得雨。 |

Evidence: `.runtime/fullsong-semantic-compare/` contains the exact candidate prompts, production-generated schemas, native outputs, measured results and QA harness. The baseline outputs remain unchanged in `.runtime/fullsong-v3-qa/`.

## Local-context + generic fidelity instruction experiment

This follow-up retained each production v3 local `background` verbatim and the original exact-ID schema. It appended only: “Preserve every quantity, negation, condition, concrete object, and action without omission or substitution. Do not add information. Translate each task line itself; background must never replace a task line.” There were no leave/scarf-specific hints, glossaries, text substitutions or product edits.

- Standard last batch: 28.215 s, strict-valid. ID 45 still reverses “leave the silver key” to “keep the silver key”. ID 42 still omits “twice”. Known serious action reversal remains a release-quality blocker.
- Compact Korean batch: 35.834 s, strict-valid. ID 33 still changes scarf to headband; ID 29 still has 等灯; ID 32 still broadens “none of us” to “no one”. The target-language collapse observed with whole-song background did not occur here, but object fidelity remains blocked.
- Both runs were below 60 seconds. Passing time/shape limits did not repair meaning. These two results do not justify adopting the generic instruction as a verified correction.

### Complete local-context candidate outputs

#### standard / en-US

| ID | Source | Local-context + fidelity candidate |
|---:|---|---|
| 36 | 我把晨光折进一只纸船。 | I fold the morning light into a paper boat. |
| 37 | 你的蓝杯子仍在窗边等候。 | Your blue cup is still waiting by the window. |
| 38 | 我并没有答应忘记你的名字。 | I have not agreed to forget your name. |
| 39 | 你说向北的路已经关闭。 | You said the road north is closed. |
| 40 | 我们带着七粒种子走过桥。 | We walk across the bridge with seven seeds. |
| 41 | 没有灯笼能代替夏天的太阳。 | No lantern can replace the summer sun. |
| 42 | 如果你回来，请轻轻敲两下门。 | If you come back, please knock gently on the door. |
| 43 | 我听见火车，却看不见灯光。 | I hear the train, but I cannot see the lights. |
| 44 | 安静的房间不属于我们任何一个人。 | The quiet room does not belong to any of us. |
| 45 | 带走红围巾，留下银钥匙。 | Take the red scarf, keep the silver key. |
| 46 | 明天我会选择另一条路。 | Tomorrow I will choose a different path. |
| 47 | 我们的小河仍然记得雨。 | Our small river still remembers the rain. |


#### compact / zh-CN

| ID | Source | Local-context + fidelity candidate |
|---:|---|---|
| 24 | 아침 햇살을 종이배에 접어 담아. | 晨光折进一只纸船。 |
| 25 | 파란 컵이 창가에서 너를 기다려. | 你的蓝杯子仍在窗边等候。 |
| 26 | 네 이름을 잊겠다고 약속하지 않았어. | 我并没有答应忘记你的名字。 |
| 27 | 북쪽 길이 닫혔다고 네가 말했어. | 你说向北的路已经关闭。 |
| 28 | 우리는 씨앗 일곱 개를 들고 다리를 건너. | 我们带着七粒种子过桥。 |
| 29 | 등불은 여름 태양을 대신할 수 없어. | 等灯无法替代夏季的太阳。 |
| 30 | 돌아오면 조용히 두 번 두드려. | 回来时安静地敲两下门。 |
| 31 | 기차 소리는 들리지만 불빛은 보이지 않아. | 能听到火车的声音，却看不见灯光。 |
| 32 | 고요한 방은 우리 누구의 것도 아니야. | 宁静的房间不是任何人的所有物。 |
| 33 | 빨간 목도리는 가져가고 은색 열쇠는 두고 가. | 带上红色的头带，留下银色钥匙。 |
| 34 | 내일 나는 다른 길을 고를 거야. | 明天我会选择另一条路。 |
| 35 | 작은 강은 아직 비를 기억해. | 小河还记着雨水的记忆。 |

Evidence: `.runtime/fullsong-local-fidelity-compare/` contains exact prompts, schemas, raw outputs, timings, build results and QA harness. The baseline and rejected full-context experiment are retained separately.

## Chinese task-instruction candidate (official-template rationale)

Tencent’s [official model card](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF#hy-mt2-translation-task-instruction-examples-chinese-english-comparison), checked on 2026-10-01, supplies Chinese/English task templates including structured-data translation and recommends full language names. This motivated one further bounded candidate; it does not substantiate any accuracy guarantee.

The source metadata, original local background, task-line data and exact-ID schema were unchanged. The general task instruction was rewritten concisely in Chinese, using 英语 or 简体中文, and requiring retention of subject/object, negation, quantities, conditions, action direction and concrete objects, no omissions/substitutions, same-language copying, and background only for disambiguation. No word-specific glossary or correction was introduced. Exactly the two preselected batches were run, once each, without searching random retries for a passing sample.

- Standard Chinese→English: 39.819 s, strict-valid. “Leave the silver key” still became “keep the silver key” (ID 45), and “twice” remained missing (ID 42). The action-reversal blocker persists.
- Compact Korean→Chinese: 58.251 s, strict-valid, near the unchanged 60-second call cap. Scarf still became headband (ID 33); the lantern/lamp noun became morning light (ID 29), another concrete-object change. The restricted “none of us” scope is still lost at ID 32.
- This candidate did not clear either known serious error and was not adopted as a verified repair. Structure/runtime acceptance and translation quality remain separate. The semantic release gate stays blocked.

### Complete Chinese-instruction candidate outputs

#### standard / en-US

| ID | Source | Chinese-instruction candidate |
|---:|---|---|
| 36 | 我把晨光折进一只纸船。 | I fold the morning light into a paper boat. |
| 37 | 你的蓝杯子仍在窗边等候。 | Your blue cup still waits by the window. |
| 38 | 我并没有答应忘记你的名字。 | I have not agreed to forget your name. |
| 39 | 你说向北的路已经关闭。 | You said the road toward the north is closed. |
| 40 | 我们带着七粒种子走过桥。 | We walk across the bridge with seven seeds. |
| 41 | 没有灯笼能代替夏天的太阳。 | No lantern can replace the summer sun. |
| 42 | 如果你回来，请轻轻敲两下门。 | If you come back, please knock gently on the door. |
| 43 | 我听见火车，却看不见灯光。 | I hear the train, but I cannot see the lights. |
| 44 | 安静的房间不属于我们任何一个人。 | The quiet room does not belong to any of us. |
| 45 | 带走红围巾，留下银钥匙。 | Take the red scarf away, keep the silver key. |
| 46 | 明天我会选择另一条路。 | Tomorrow I will choose a different path. |
| 47 | 我们的小河仍然记得雨。 | Our small river still remembers the rain. |


#### compact / zh-CN

| ID | Source | Chinese-instruction candidate |
|---:|---|---|
| 24 | 아침 햇살을 종이배에 접어 담아. | 将晨光折进一只纸船。 |
| 25 | 파란 컵이 창가에서 너를 기다려. | 蓝色杯子在窗边等待你。 |
| 26 | 네 이름을 잊겠다고 약속하지 않았어. | 我没有承诺会忘记你的名字。 |
| 27 | 북쪽 길이 닫혔다고 네가 말했어. | 你说向北的路已经关闭了。 |
| 28 | 우리는 씨앗 일곱 개를 들고 다리를 건너. | 我们带着七颗种子跨越桥梁。 |
| 29 | 등불은 여름 태양을 대신할 수 없어. | 晨光无法替代夏季的太阳。 |
| 30 | 돌아오면 조용히 두 번 두드려. | 回来后安静地敲两下门。 |
| 31 | 기차 소리는 들리지만 불빛은 보이지 않아. | 能听到火车的声音，却看不到灯光。 |
| 32 | 고요한 방은 우리 누구의 것도 아니야. | 宁静的房间不是任何人的所有物。 |
| 33 | 빨간 목도리는 가져가고 은색 열쇠는 두고 가. | 带走红色头带，留下银色钥匙。 |
| 34 | 내일 나는 다른 길을 고를 거야. | 明天我会选择另一条路。 |
| 35 | 작은 강은 아직 비를 기억해. | 小河流水仍然记得雨水。 |

Evidence: `.runtime/fullsong-chinese-instruction-compare/` contains exact Chinese prompts, schemas, raw model outputs, timing/strict-validity results and the QA harness. All prior failed/candidate evidence remains preserved; no product implementation was edited by these QA tests.

## Final single-line diagnostic: no context, no JSON, no schema

This was a predeclared diagnostic, not another release candidate or a search for a passing random sample. Each existing local model ran exactly once with the official default Chinese translation-template wording and the original problematic source line. There was no background, JSON wrapper, grammar or JSON-schema constraint. Target names were 英语 and 简体中文. Offline CPU arguments, four threads, 4096-token context, 2048 generation cap, temperature 0.1, non-mmap loading and a 60-second watchdog remained; model-specific RSS watchdogs were 3 GiB standard and 1.5 GiB compact. The pinned compact EOS override was retained. No download or product-source edit occurred.

| Model | Original source | Raw semantic output, excluding runtime terminator | Time | Result |
|---|---|---|---:|---|
| Standard Q4, Chinese→English | 带走红围巾，留下银钥匙。 | Take away the red scarf, and keep the silver key. | 11.644 s | Key action still reversed from leave behind to keep |
| Compact IQ3_S, Korean→Chinese | 빨간 목도리는 가져가고 은색 열쇠는 두고 가. | 拿走红色的颈链，把银色的钥匙留下。 | 7.595 s | Scarf changed to necklace; key action preserved |

Both processes exited normally with no watchdog trigger. Sampled peak RSS was 1,463,390,208 bytes standard and 1,204,846,592 bytes compact. Raw stdout, including `[end of text]`, is preserved without alteration.

The errors are therefore reproducible outside structured batching for these exact samples; they cannot be explained solely by the batch JSON/schema requirement or surrounding-context pressure. This limited test does not isolate quantization versus base-model versus sampling effects or prove model-wide language incompetence. It does reinforce the current known-serious-semantic-error quality blocker. No release clearance follows, and candidate probing stopped after these two planned runs.

Evidence: `.runtime/single-line-diagnostic/` contains both exact prompts, CLI argument lists, raw stdout/stderr, timing/RSS results and the sequential watchdog harness.
