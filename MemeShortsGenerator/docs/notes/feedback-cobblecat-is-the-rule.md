---
name: feedback-cobblecat-is-the-rule
description: "For the cobblecat channel, anything CobbleCat himself doesn't do, our videos shouldn't do either - check his transcripts before allowing a writing habit"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 9584ff37-85a0-4ea1-b89a-4714dc776da1
  modified: 2026-09-29T21:07:18.805Z
---

Matthew, 2026-09-30: "as long as cobble cat does not do those don't do those as well". Said about
lines that write an object as a person ("my typo got a manager!") - he pushed back when I called
it a "human trait" quip: people don't talk like that.

**Why:** the goal is his format and voice, not "funny in general". Written-sounding punchlines are
exactly what makes our scripts read as AI.

**How to apply:** before keeping any writing habit, test it against his 20 transcripts
(scratchpad `refs/*.words.json`): if he never does it, block it in code, not just in the prompt -
prompt rules alone kept failing. Pattern checks must show 0 hits on his transcripts (the
person-as-joke regex: 0 of 20 his, 8 in our 35). Talking TO the object is his; describing it as a
person is not. Use plain words with him - "human trait" confused him. See [[cobblecat-format-study]].
