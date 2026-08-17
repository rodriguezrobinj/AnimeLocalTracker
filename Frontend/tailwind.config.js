/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  theme: {
    extend: {
      colors: {
        background: '#121212',
        surface: '#1E1E1E',
        surfaceLight: '#2C2C2C',
        primary: '#673AB7', // Indigo/Purple
        accent: '#E91E63', // Pink
        textMain: '#FFFFFF',
        textMuted: '#9E9E9E'
      }
    },
  },
  plugins: [],
}
