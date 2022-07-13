
const schema = `schema {
  query: Query
  mutation: Mutation
}

type Movie {
  title: String
  simpleTitle: String
  rating: String
  releaseDate: DateTime
  runtime: String
  genre: String
  director: String
  writer: String
  actors: String
  plot: String
  posterLink: String
  imdbRating: Decimal
  imdbID: String
  tomatoRating: Int
  uploadedDate: DateTime
  id: Int!
  viewings: [Viewing!]!
}

type Mutation {
  runEmptyTest: Boolean!
}

type Query {
  movies(where: MovieFilterInput order: [MovieSortInput!]): [Movie]
}

type User {
  userID: Int!
  username: String
}

type Viewing {
  viewingID: Int!
  movieID: Int!
  movie: Movie!
  userID: Int!
  user: User!
  viewingType: String
  viewingData: String
}

input ComparableInt32OperationFilterInput {
  eq: Int
  neq: Int
  in: [Int!]
  nin: [Int!]
  gt: Int
  ngt: Int
  gte: Int
  ngte: Int
  lt: Int
  nlt: Int
  lte: Int
  nlte: Int
}

input ComparableNullableOfDateTimeOperationFilterInput {
  eq: DateTime
  neq: DateTime
  in: [DateTime]
  nin: [DateTime]
  gt: DateTime
  ngt: DateTime
  gte: DateTime
  ngte: DateTime
  lt: DateTime
  nlt: DateTime
  lte: DateTime
  nlte: DateTime
}

input ComparableNullableOfDecimalOperationFilterInput {
  eq: Decimal
  neq: Decimal
  in: [Decimal]
  nin: [Decimal]
  gt: Decimal
  ngt: Decimal
  gte: Decimal
  ngte: Decimal
  lt: Decimal
  nlt: Decimal
  lte: Decimal
  nlte: Decimal
}

input ComparableNullableOfInt32OperationFilterInput {
  eq: Int
  neq: Int
  in: [Int]
  nin: [Int]
  gt: Int
  ngt: Int
  gte: Int
  ngte: Int
  lt: Int
  nlt: Int
  lte: Int
  nlte: Int
}

input ListFilterInputTypeOfViewingFilterInput {
  all: ViewingFilterInput
  none: ViewingFilterInput
  some: ViewingFilterInput
  any: Boolean
}

input MovieFilterInput {
  and: [MovieFilterInput!]
  or: [MovieFilterInput!]
  title: StringOperationFilterInput
  simpleTitle: StringOperationFilterInput
  rating: StringOperationFilterInput
  releaseDate: ComparableNullableOfDateTimeOperationFilterInput
  runtime: StringOperationFilterInput
  genre: StringOperationFilterInput
  director: StringOperationFilterInput
  writer: StringOperationFilterInput
  actors: StringOperationFilterInput
  plot: StringOperationFilterInput
  posterLink: StringOperationFilterInput
  imdbRating: ComparableNullableOfDecimalOperationFilterInput
  imdbID: StringOperationFilterInput
  tomatoRating: ComparableNullableOfInt32OperationFilterInput
  uploadedDate: ComparableNullableOfDateTimeOperationFilterInput
  id: ComparableInt32OperationFilterInput
  viewings: ListFilterInputTypeOfViewingFilterInput
}

input MovieSortInput {
  title: SortEnumType
  simpleTitle: SortEnumType
  rating: SortEnumType
  releaseDate: SortEnumType
  runtime: SortEnumType
  genre: SortEnumType
  director: SortEnumType
  writer: SortEnumType
  actors: SortEnumType
  plot: SortEnumType
  posterLink: SortEnumType
  imdbRating: SortEnumType
  imdbID: SortEnumType
  tomatoRating: SortEnumType
  uploadedDate: SortEnumType
  id: SortEnumType
}

input StringOperationFilterInput {
  and: [StringOperationFilterInput!]
  or: [StringOperationFilterInput!]
  eq: String
  neq: String
  contains: String
  ncontains: String
  in: [String]
  nin: [String]
  startsWith: String
  nstartsWith: String
  endsWith: String
  nendsWith: String
}

input UserFilterInput {
  and: [UserFilterInput!]
  or: [UserFilterInput!]
  userID: ComparableInt32OperationFilterInput
  username: StringOperationFilterInput
}

input ViewingFilterInput {
  and: [ViewingFilterInput!]
  or: [ViewingFilterInput!]
  viewingID: ComparableInt32OperationFilterInput
  movieID: ComparableInt32OperationFilterInput
  movie: MovieFilterInput
  userID: ComparableInt32OperationFilterInput
  user: UserFilterInput
  viewingType: StringOperationFilterInput
  viewingData: StringOperationFilterInput
}

enum ApplyPolicy {
  BEFORE_RESOLVER
  AFTER_RESOLVER
}

enum SortEnumType {
  ASC
  DESC
}

directive @authorize("The name of the authorization policy that determines access to the annotated resource." policy: String "Roles that are allowed to access the annotated resource." roles: [String!] "Defines when when the resolver shall be executed.By default the resolver is executed after the policy has determined that the current user is allowed to access the field." apply: ApplyPolicy! = BEFORE_RESOLVER) repeatable on SCHEMA | OBJECT | FIELD_DEFINITION

"The \`@defer\` directive may be provided for fragment spreads and inline fragments to inform the executor to delay the execution of the current fragment to indicate deprioritization of the current fragment. A query with \`@defer\` directive will cause the request to potentially return multiple responses, where non-deferred data is delivered in the initial response and data deferred is delivered in a subsequent response. \`@include\` and \`@skip\` take precedence over \`@defer\`."
directive @defer("If this argument label has a value other than null, it will be passed on to the result of this defer directive. This label is intended to give client applications a way to identify to which fragment a deferred result belongs to." label: String "Deferred when true." if: Boolean) on FRAGMENT_SPREAD | INLINE_FRAGMENT

"The \`@specifiedBy\` directive is used within the type system definition language to provide a URL for specifying the behavior of custom scalar definitions."
directive @specifiedBy("The specifiedBy URL points to a human-readable specification. This field will only read a result for scalar types." url: String!) on SCALAR

"The \`@stream\` directive may be provided for a field of \`List\` type so that the backend can leverage technology such as asynchronous iterators to provide a partial list in the initial response, and additional list items in subsequent responses. \`@include\` and \`@skip\` take precedence over \`@stream\`."
directive @stream("If this argument label has a value other than null, it will be passed on to the result of this stream directive. This label is intended to give client applications a way to identify to which fragment a streamed result belongs to." label: String "The initial elements that shall be send down to the consumer." initialCount: Int! = 0 "Streamed when true." if: Boolean) on FIELD

scalar Any

"The \`DateTime\` scalar represents an ISO-8601 compliant date time type."
scalar DateTime @specifiedBy(url: "https:\/\/www.graphql-scalars.com\/date-time")

"The built-in \`Decimal\` scalar type."
scalar Decimal`

export default schema