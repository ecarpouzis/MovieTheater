import { ApolloLink } from '@apollo/client'

function fixUtcDates(obj) {
    if (!obj) return

    if (obj instanceof Date) {
        const utcHours = obj.getUTCHours()
        const utcMinutes = obj.getUTCMinutes()
        const utcSeconds = obj.getUTCSeconds()
        const utcMilliseconds = obj.getUTCMilliseconds()
        if (utcHours === 0 && utcMinutes === 0 && utcSeconds === 0 && utcMilliseconds === 0) {
            const utcDate = obj.getUTCDate()
            const utcMonth = obj.getUTCMonth()
            const utcYear = obj.getUTCFullYear()
            obj.setDate(utcDate)
            obj.setMonth(utcMonth)
            obj.setFullYear(utcYear)
            obj.setHours(0)
            obj.setMinutes(0)
            obj.setSeconds(0)
            obj.setMilliseconds(0)
        }
        return
    }

    if (obj instanceof Array) {
        obj.forEach((x) => fixUtcDates(x))
    } else if (typeof obj === 'object') {
        Object.keys(obj).forEach((key) => fixUtcDates(obj[key]))
    }
}

export default new ApolloLink((operation, forward) => {
    const result = forward(operation)
    return result.map((data) => {
        fixUtcDates(data)
        return data
    })
})
