import { Routes, Route } from 'react-router-dom'

// Routes are added here as features are built.
// Auth routes and the protected chat shell are wired in the auth flow step.
export default function App() {
  return (
    <Routes>
      <Route path="/" element={<p className="p-4 text-gray-500">ChatNET</p>} />
    </Routes>
  )
}
