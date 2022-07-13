const fs = require('fs')
const sdl = fs.readFileSync('./src/schema.graphql', { encoding: 'utf8' })

module.exports = {
    parser: 'babel-eslint',
    rules: {
        'graphql/template-strings': [
            'error',
            {
                env: 'apollo',
                schemaString: sdl,
            },
        ],
    },
    plugins: ['graphql'],
}
