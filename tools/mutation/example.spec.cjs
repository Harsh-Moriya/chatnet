// A small spec that shows the format and doubles as a check that the runner works: the first two mutants should be
// KILLED and the third should not COMPILE. If one reports NOAPPLY, the code it breaks has changed; update its `old` text.
const RULES = 'frontend/src/features/chat/groupRules.ts'
const EVICTOR = 'backend/ChatNET.API/Messaging/Hubs/ConnectionEvictor.cs'
const EVICTION_TESTS = 'FullyQualifiedName~ConnectionEvictionTests'

module.exports = [
  {
    name: 'room-left-has-no-floor',
    area: 'fe',
    file: RULES,
    old: 'return Math.max(0, MAX_GROUP_MEMBERS - memberCount)',
    new: 'return MAX_GROUP_MEMBERS - memberCount',
    // The test file for the rule first, the whole suite only if that one passes.
    stages: [['src/features/chat/groupRules.test.ts'], []],
  },
  {
    name: 'typing-gate-kept-after-removal',
    area: 'be',
    file: EVICTOR,
    old: `                ConnectionState.Forget(connection.Items, conversationId);
`,
    new: '',
    stages: ['FullyQualifiedName~A_removed_member_cannot_make_the_group_hear_them_type', EVICTION_TESTS],
  },
  {
    name: 'does-not-compile',
    area: 'be',
    file: EVICTOR,
    old: 'var connections = _registry.Of(userId);',
    new: 'var connections = _registry.Of(userId).Take("one").ToArray();',
    stages: [EVICTION_TESTS],
  },
]
