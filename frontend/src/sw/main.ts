import { installWorker, type WorkerScope } from './worker'

// The entry of the service worker bundle (vite.sw.config.ts): the one file that touches the worker's own scope, which is what this
// compilation (tsconfig.sw.json) is for.
installWorker(self as unknown as WorkerScope)
