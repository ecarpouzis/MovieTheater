import schema from "../schema";
import { buildSchema } from "graphql";
import { ApolloClient, InMemoryCache, createHttpLink, ApolloLink } from "@apollo/client";
import { setContext } from "@apollo/client/link/context";
import { withScalars } from "apollo-link-scalars";
import { DateTimeResolver } from "graphql-scalars";
import FixDatesApolloLink from "./FixDatesApolloLink";

const apolloLinks = ApolloLink.from([
  setContext((_, { headers }) => {
    return {
      headers: {
        ...headers,
      },
    };
  }),
  FixDatesApolloLink,
  withScalars({
    schema: buildSchema(schema),
    typesMap: {
      DateTime: DateTimeResolver,
    },
  }),
  createHttpLink({
    uri: "/graphql",
  }),
]);

const client = new ApolloClient({
  link: apolloLinks,
  credentials: "same-origin",
  cache: new InMemoryCache(),
});

export default client;
