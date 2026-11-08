import { describe, expect, it, vi } from 'vitest'
import { fromHex, toHex, utf8 } from './bytes'
import {
  aesGcmDecrypt,
  aesGcmEncrypt,
  ed25519FromSeed,
  ed25519Sign,
  ed25519Verify,
  hkdf,
  pbkdf2,
  sha256,
  x25519Derive,
  x25519FromPrivate,
} from './primitives'

// Known answers from the published standards, so the wrappers are checked against something
// other than this code.

describe('SHA-256 (FIPS 180-4)', () => {
  it('hashes "abc"', async () => {
    expect(toHex(await sha256(utf8('abc')))).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
  })
})

describe('HKDF-SHA256 (RFC 5869, test case 1)', () => {
  it('derives the output keying material', async () => {
    const okm = await hkdf(
      fromHex('0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b'),
      fromHex('000102030405060708090a0b0c'),
      fromHex('f0f1f2f3f4f5f6f7f8f9'),
      42,
    )

    expect(toHex(okm)).toBe('3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865')
  })

  // RFC 5869 test case 3: an empty salt behaves as a string of zeros, which the commitment relies on.
  it('accepts an empty salt (test case 3)', async () => {
    const okm = await hkdf(fromHex('0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b'), new Uint8Array(0), new Uint8Array(0), 42)

    expect(toHex(okm)).toBe('8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8')
  })
})

describe('PBKDF2-HMAC-SHA256', () => {
  it.each([
    [1, '120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b'],
    [2, 'ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43'],
    [4096, 'c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a'],
  ])('derives with %i iterations', async (iterations, expected) => {
    expect(toHex(await pbkdf2(utf8('password'), utf8('salt'), iterations, 32))).toBe(expected)
  })
})

describe('X25519 (RFC 7748, section 6.1)', () => {
  const alicePrivate = fromHex('77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a')
  const alicePublic = '8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a'
  const bobPrivate = fromHex('5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb')
  const bobPublic = 'de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f'
  const shared = '4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742'

  it('computes the public keys', async () => {
    expect(toHex((await x25519FromPrivate(alicePrivate)).publicKey)).toBe(alicePublic)
    expect(toHex((await x25519FromPrivate(bobPrivate)).publicKey)).toBe(bobPublic)
  })

  it('agrees on the shared secret from both sides', async () => {
    expect(toHex(await x25519Derive(alicePrivate, fromHex(bobPublic)))).toBe(shared)
    expect(toHex(await x25519Derive(bobPrivate, fromHex(alicePublic)))).toBe(shared)
  })

  it('refuses a public key of small order', async () => {
    await expect(x25519Derive(alicePrivate, new Uint8Array(32))).rejects.toMatchObject({ name: 'CryptoError' })
  })

  // Node's WebCrypto throws for a small-order point. Some browsers return a secret of zeros
  // instead, which must be refused just the same.
  it('refuses an all-zero secret when the browser returns one', async () => {
    const derive = vi.spyOn(crypto.subtle, 'deriveBits').mockResolvedValueOnce(new ArrayBuffer(32))

    await expect(x25519Derive(alicePrivate, fromHex(bobPublic))).rejects.toMatchObject({ name: 'CryptoError', code: 'malformed' })
    expect(derive).toHaveBeenCalledTimes(1)
    derive.mockRestore()
  })
})

describe('Ed25519 (RFC 8032, test 1 and test 2)', () => {
  it('computes the public key and signs the empty message', async () => {
    const seed = fromHex('9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60')
    const pair = await ed25519FromSeed(seed)
    const signature = await ed25519Sign(seed, new Uint8Array(0))

    expect(toHex(pair.publicKey)).toBe('d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a')
    expect(toHex(signature)).toBe(
      'e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b',
    )
    expect(await ed25519Verify(pair.publicKey, new Uint8Array(0), signature)).toBe(true)
  })

  it('signs a one byte message', async () => {
    const seed = fromHex('4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb')
    const signature = await ed25519Sign(seed, fromHex('72'))

    expect(toHex((await ed25519FromSeed(seed)).publicKey)).toBe('3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c')
    expect(toHex(signature)).toBe(
      '92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00',
    )
  })

  it('rejects a changed message and a changed signature', async () => {
    const pair = await ed25519FromSeed(fromHex('9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60'))
    const signature = await ed25519Sign(pair.privateKey, utf8('hello'))
    const flipped = signature.slice()
    flipped[10] ^= 1

    expect(await ed25519Verify(pair.publicKey, utf8('hellO'), signature)).toBe(false)
    expect(await ed25519Verify(pair.publicKey, utf8('hello'), flipped)).toBe(false)
    expect(await ed25519Verify(pair.publicKey, utf8('hello'), signature.slice(0, 63))).toBe(false)
  })
})

// WebCrypto takes any valid AES key size and any IV length, so a key or nonce of the wrong size
// would silently become AES-128 or a non-standard IV unless it is refused here.
describe('AES-GCM input sizes', () => {
  const nonce = new Uint8Array(12)
  const aad = new Uint8Array(0)
  const sealed = new Uint8Array(32)

  it.each([16, 24, 31, 33])('refuses a key of %i bytes', async (length) => {
    const key = new Uint8Array(length)

    await expect(aesGcmEncrypt(key, nonce, sealed, aad)).rejects.toMatchObject({ code: 'malformed' })
    await expect(aesGcmDecrypt(key, nonce, sealed, aad)).rejects.toMatchObject({ code: 'malformed' })
  })

  it.each([0, 8, 11, 13, 16])('refuses a nonce of %i bytes', async (length) => {
    const key = new Uint8Array(32)

    await expect(aesGcmEncrypt(key, new Uint8Array(length), sealed, aad)).rejects.toMatchObject({ code: 'malformed' })
    await expect(aesGcmDecrypt(key, new Uint8Array(length), sealed, aad)).rejects.toMatchObject({ code: 'malformed' })
  })
})

describe('AES-256-GCM (McGrew and Viega, test cases 15 and 16)', () => {
  const key = fromHex('feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308')
  const nonce = fromHex('cafebabefacedbaddecaf888')
  const plain = fromHex(
    'd9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b391aafd255',
  )

  it('encrypts without associated data (test case 15)', async () => {
    const sealed = await aesGcmEncrypt(key, nonce, plain, new Uint8Array(0))

    expect(toHex(sealed)).toBe(
      '522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662898015ad' +
        'b094dac5d93471bdec1a502270e3cc6c',
    )
    expect(toHex(await aesGcmDecrypt(key, nonce, sealed, new Uint8Array(0)))).toBe(toHex(plain))
  })

  it('authenticates associated data (test case 16 uses 20 bytes of it)', async () => {
    const aad = fromHex('feedfacedeadbeeffeedfacedeadbeefabaddad2')
    const sealed = await aesGcmEncrypt(key, nonce, plain.subarray(0, 60), aad)

    expect(toHex(sealed.subarray(60))).toBe('76fc6ece0f4e1768cddf8853bb2d551b')
    await expect(aesGcmDecrypt(key, nonce, sealed, fromHex('feedfacedeadbeeffeedfacedeadbeefabaddad3'))).rejects.toMatchObject({ code: 'authentication' })
  })
})
