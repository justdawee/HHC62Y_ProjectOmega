/** @type {import('tailwindcss').Config} */
module.exports = {
  darkMode: 'class',
  content: [
    './*.fs',
    './*.html',
    './styles/**/*.css'
  ],
  theme: {
    extend: {
      fontFamily: {
        sans: ['Inter', 'ui-sans-serif', 'system-ui', 'sans-serif'],
        serif: ['"Instrument Serif"', 'ui-serif', 'Georgia', 'serif'],
        mono: ['"JetBrains Mono"', 'ui-monospace', 'monospace']
      },
      keyframes: {
        drift: {
          '0%, 100%': { transform: 'translate(0,0) scale(1)' },
          '33%':      { transform: 'translate(40px, -30px) scale(1.05)' },
          '66%':      { transform: 'translate(-30px, 40px) scale(0.95)' }
        },
        shimmer: {
          '0%, 100%': { 'background-position': '0% 50%' },
          '50%':      { 'background-position': '100% 50%' }
        },
        shimmerSlide: {
          '0%':   { transform: 'translateX(-100%)' },
          '100%': { transform: 'translateX(100%)' }
        },
        rotateBorder: {
          'from': { 'background-position': '0% 0%' },
          'to':   { 'background-position': '300% 0%' }
        },
        softPulse: {
          '0%, 100%': { opacity: '1', transform: 'scale(1)' },
          '50%':      { opacity: '0.5', transform: 'scale(1.4)' }
        },
        addPop: {
          '0%':   { opacity: '0', transform: 'scale(0.85) translateY(8px)' },
          '60%':  { opacity: '1', transform: 'scale(1.04) translateY(0)' },
          '100%': { opacity: '1', transform: 'scale(1) translateY(0)' }
        },
        removePop: {
          '0%':   { opacity: '1', transform: 'scale(1)' },
          '100%': { opacity: '0', transform: 'scale(0.7)' }
        },
        modalIn: {
          '0%':   { opacity: '0', transform: 'scale(0.96) translateY(8px)' },
          '100%': { opacity: '1', transform: 'scale(1) translateY(0)' }
        },
        backdropIn: {
          '0%':   { opacity: '0' },
          '100%': { opacity: '1' }
        },
        stepIn: {
          '0%':   { opacity: '0', transform: 'translateY(6px)' },
          '100%': { opacity: '1', transform: 'translateY(0)' }
        },
        shake: {
          '0%, 100%': { transform: 'translateX(0)' },
          '25%':      { transform: 'translateX(-4px)' },
          '50%':      { transform: 'translateX(4px)' },
          '75%':      { transform: 'translateX(-2px)' }
        }
      },
      animation: {
        drift:        'drift 20s ease-in-out infinite',
        shimmer:      'shimmer 8s ease-in-out infinite',
        shimmerSlide: 'shimmerSlide 1.6s ease-in-out infinite',
        rotateBorder: 'rotateBorder 4s linear infinite',
        softPulse:    'softPulse 1.6s ease-in-out infinite',
        addPop:       'addPop 280ms cubic-bezier(.2,.7,.2,1) both',
        removePop:    'removePop 200ms ease-in forwards',
        modalIn:      'modalIn 280ms cubic-bezier(.2,.7,.2,1) both',
        backdropIn:   'backdropIn 200ms ease-out both',
        stepIn:       'stepIn 280ms cubic-bezier(.2,.7,.2,1) both',
        shake:        'shake 280ms ease-in-out'
      },
      transitionTimingFunction: {
        'out-expo': 'cubic-bezier(0.16, 1, 0.3, 1)',
        'soft':     'cubic-bezier(.2,.7,.2,1)'
      }
    }
  },
  plugins: [
    // strategy: 'class' — the plugin styles ONLY elements with the
    // .form-input / .form-checkbox / etc. classes, instead of branding
    // every native <input> with its blue focus ring + grey border.
    require('@tailwindcss/forms')({ strategy: 'class' }),
    require('@tailwindcss/typography')
  ]
};
