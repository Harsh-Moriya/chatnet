# Safety numbers

Everything is sealed to keys that the server hands out, so the server could, if it were dishonest, hand one person a key of its own for another. A safety number is how two people check that it did not: a number made from both of their identity keys that each can read from their own screen, and that is the same on both screens only if each holds the real key of the other.

The number is specified in the [encryption formats](../architecture/encryption-format.md#safety-number). This page is how it is used.

## Behaviour

- **Where.** A direct chat has a Safety number button in its header. In a group, each other member has a Safety number item in their menu in the group info.
- **What is shown.** Sixty digits in twelve groups of five, the name of the person, and how to compare. The number is made from the identity key of the person looking and the identity key this browser remembers for the other person, so it is the same on the other person's screen. If the server had given either of them a key that is not the other's real one, the two numbers differ.
- **How to compare.** Out loud on a call, in person, or over another channel the server does not carry. If every group matches, nobody is between the two people.
- **Verified.** After comparing, the person looking can mark the other as verified, and take the mark away again. A verified person shows a mark on the button in the header of a direct chat. The mark belongs to the key it was made for, and it is kept in this browser only: another device of the same person starts without it.
- **When a key changes.** The first time a person's identity key is seen it is remembered. A different key for the same person (they started over, or someone is between the two) is not used. The conversation shows a notice for each person whose key changed, with Compare and accept. The dialog then shows the number for the key now offered, with a warning, and a button to accept it. Until it is accepted nothing is sealed to the person and what they sign is not trusted, so their new messages show as one that cannot be verified.
- **Accepting.** The key now offered replaces the remembered one, the verified mark is gone, the conversations that could not make their keys for the person try again and the messages that were shown as sent by someone whose key changed are opened again.
- **A person who has not set up encryption** has no key, and the dialog says so.

## When a key changes without anyone being between them

A person's identity key changes when they start over (see [encryption keys](encryption-keys.md)). Removing a lost device does not change it. So a change is expected only after someone says they started over, and the way to check is the number: compare it with them before accepting.

## Limits

- Accepting a new key is for messages from then on: what the person signed with their old key cannot be verified against the new one, so their earlier messages show as ones that cannot be verified.
- Accepting is done in each browser. A person's other devices ask for it on their own.
- The first sight of a person's identity key is trusted. A server that was dishonest from the start is only noticed by comparing the number.
- The numbers are as good as the way they are compared. A number sent over the same app proves nothing.
- A verified mark is not shared between a person's devices, and is lost when the browser's data is, as the remembered keys are.
- Seeing a key change needs the person to open a conversation with them: nothing is pushed to tell people in advance.
