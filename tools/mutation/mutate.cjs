// Parallel mutation runner. A mutation check breaks one line of the code on purpose and runs the tests that are meant to
// notice; a test suite that stays green over a broken rule is not testing that rule. Each worker has its own copy of the
// repository, so mutants run side by side and the real working tree is never edited.
//
//   node tools/mutation/mutate.cjs <spec.cjs> [--workers 3] [--work <directory>] [--out <results.jsonl>]
//
// See README.md for the spec format and the verdicts.
const fs = require('node:fs')
const os = require('node:os')
const path = require('node:path')
const { spawn, execFileSync } = require('node:child_process')

// ##### Options #####

const REPO = path.resolve(__dirname, '..', '..')

const args = process.argv.slice(2)
const option = (name, fallback) => (args.includes(name) ? args[args.indexOf(name) + 1] : fallback)

if (args.length === 0 || args[0].startsWith('--')) {
  console.error('usage: node tools/mutation/mutate.cjs <spec.cjs> [--workers 3] [--work <directory>] [--out <results.jsonl>]')
  process.exit(2)
}

const specPath = path.resolve(args[0])
const workerCount = Number(option('--workers', '3'))
const workRoot = path.resolve(option('--work', path.join(os.tmpdir(), 'chatnet-mutation')))
const outPath = path.resolve(option('--out', path.join(workRoot, 'results.jsonl')))
const mutants = require(specPath)

// ##### Worker copies #####

// Generated or installed folders are never copied: a worker builds its own, and keeps them between runs so the next run
// only compiles what changed.
const SKIPPED = new Set(['bin', 'obj', 'node_modules', 'dist', '.vite'])

// Makes `to` hold the same files as `from`, touching only files whose content differs. A copied file gets a new
// modification time, which is what makes the build compile it again.
function mirror(from, to) {
  fs.mkdirSync(to, { recursive: true })
  const wanted = fs.readdirSync(from).filter((name) => !SKIPPED.has(name))

  for (const name of fs.readdirSync(to)) {
    if (!SKIPPED.has(name) && !wanted.includes(name)) fs.rmSync(path.join(to, name), { recursive: true, force: true })
  }

  for (const name of wanted) {
    const source = path.join(from, name)
    const target = path.join(to, name)

    if (fs.statSync(source).isDirectory()) {
      mirror(source, target)
    } else if (!fs.existsSync(target) || !fs.readFileSync(source).equals(fs.readFileSync(target))) {
      fs.copyFileSync(source, target)
    }
  }
}

function syncWorker(dir) {
  mirror(path.join(REPO, 'backend'), path.join(dir, 'backend'))
  mirror(path.join(REPO, 'frontend'), path.join(dir, 'frontend'))

  const modules = path.join(dir, 'frontend', 'node_modules')
  if (!fs.existsSync(modules)) fs.symlinkSync(path.join(REPO, 'frontend', 'node_modules'), modules, 'junction')
}

// ##### Running tests #####

function run(command, argv, cwd) {
  return new Promise((resolve) => {
    const child = spawn(command, argv, { cwd, env: { ...process.env, DOTNET_ReadyToRun: '0', CI: '1' } })
    let output = ''
    child.stdout.on('data', (data) => { output += data; if (output.length > 400000) output = output.slice(-200000) })
    child.stderr.on('data', (data) => { output += data })
    child.on('error', (error) => resolve({ code: -1, output: String(error) }))
    child.on('close', (code) => resolve({ code, output }))
  })
}

const lastLines = (output) => output.trim().split('\n').slice(-3).join(' | ').slice(0, 200)

async function backendStage(dir, filter) {
  const { output } = await run('dotnet', ['test', 'ChatNET.API.Tests', '--configuration', 'Release', '--filter', filter], path.join(dir, 'backend'))

  if (/error CS\d+/.test(output)) return { verdict: 'COMPILE', note: (output.match(/error CS\d+[^\n]*/) || [''])[0].slice(0, 160) }

  const failed = output.match(/Failed!\s+-\s+Failed:\s+(\d+), Passed:\s+(\d+)/)
  if (failed) return { verdict: 'KILLED', note: `${failed[1]} failed of ${Number(failed[1]) + Number(failed[2])}` }

  const passed = output.match(/Passed!\s+-\s+Failed:\s+0, Passed:\s+(\d+)/)
  if (passed) return { verdict: 'PASSED', note: `${passed[1]} passed` }

  return { verdict: 'INCONCLUSIVE', note: lastLines(output) }
}

async function frontendStage(dir, files) {
  const cwd = path.join(dir, 'frontend')
  const { output } = await run(process.execPath, [path.join('node_modules', 'vitest', 'vitest.mjs'), 'run', ...files], cwd)

  if (/Tests\s+\d+ failed/.test(output) || /Test Files\s+\d+ failed/.test(output)) {
    return { verdict: 'KILLED', note: (output.match(/Tests\s+[^\n]*/) || [''])[0].trim() }
  }

  const passed = output.match(/Tests\s+(?:\d+ failed \| )?(\d+) passed/)
  if (passed) return { verdict: 'PASSED', note: `${passed[1]} passed` }

  return { verdict: 'INCONCLUSIVE', note: lastLines(output) }
}

// ##### One mutant #####

async function runMutant(mutant, dir) {
  const started = Date.now()
  const file = path.join(dir, mutant.file)
  const clean = fs.readFileSync(file, 'utf8')

  const occurrences = clean.split(mutant.old).length - 1
  if (occurrences !== 1) return { name: mutant.name, verdict: 'NOAPPLY', note: `${occurrences} matches`, seconds: 0 }

  fs.writeFileSync(file, clean.replace(mutant.old, () => mutant.new))

  let result = { verdict: 'INCONCLUSIVE', note: 'no stage ran' }
  let stageNumber = 0

  try {
    // A mutant is dead as soon as one stage fails, so the cheap test aimed at the rule goes first.
    for (const stage of mutant.stages) {
      stageNumber++
      result = mutant.area === 'be' ? await backendStage(dir, stage) : await frontendStage(dir, stage)
      if (result.verdict !== 'PASSED') break
    }
  } finally {
    fs.writeFileSync(file, clean)
    if (fs.readFileSync(file, 'utf8') !== clean) throw new Error(`could not restore ${file}`)
  }

  const verdict = result.verdict === 'PASSED' ? 'SURVIVED' : result.verdict
  return { name: mutant.name, verdict, stage: stageNumber, note: result.note, seconds: Math.round((Date.now() - started) / 1000) }
}

// ##### The run #####

const treeState = () => execFileSync('git', ['-C', REPO, 'status', '--porcelain'], { encoding: 'utf8' }).trim()

async function main() {
  const before = treeState()
  if (before !== '') console.log(`warning: the working tree has uncommitted changes; the workers copy them as they are:\n${before}`)

  const begun = Date.now()
  const seconds = () => Math.round((Date.now() - begun) / 1000)

  fs.mkdirSync(workRoot, { recursive: true })
  const dirs = Array.from({ length: workerCount }, (_, i) => path.join(workRoot, `worker-${i}`))
  dirs.forEach(syncWorker)
  console.log(`${workerCount} workers synced in ${seconds()} s`)

  // Every worker builds once, all at the same time, so the first mutant of each is not charged for the restore and build.
  await Promise.all(dirs.map((dir) => run('dotnet', ['build', 'ChatNET.API.Tests', '--configuration', 'Release'], path.join(dir, 'backend'))))
  console.log(`built after ${seconds()} s`)

  fs.writeFileSync(outPath, '')
  const queue = [...mutants]
  const results = []

  await Promise.all(dirs.map(async (dir) => {
    for (let mutant = queue.shift(); mutant; mutant = queue.shift()) {
      const result = await runMutant(mutant, dir)
      results.push(result)
      fs.appendFileSync(outPath, `${JSON.stringify(result)}\n`)
      console.log(`${result.verdict.padEnd(12)} ${result.name} (${result.seconds} s${result.stage ? `, stage ${result.stage}` : ''}) ${result.note}`)
    }
  }))

  const count = (verdict) => results.filter((r) => r.verdict === verdict).length
  console.log(`\n${results.length} mutants in ${seconds()} s: ${count('KILLED')} killed, ${count('SURVIVED')} survived, ${count('COMPILE')} did not compile, ${count('NOAPPLY')} did not apply, ${count('INCONCLUSIVE')} inconclusive`)

  const after = treeState()
  console.log(after === before ? 'the real working tree is unchanged' : `THE REAL WORKING TREE CHANGED DURING THE RUN:\n${after}`)
}

main().catch((error) => { console.error(error); process.exit(1) })
